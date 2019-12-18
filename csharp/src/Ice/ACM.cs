//
// Copyright (c) ZeroC, Inc. All rights reserved.
//

namespace IceInternal
{
    using System.Collections.Generic;
    using System.Diagnostics;

    public sealed class ACMConfig
    {
        internal ACMConfig(bool server)
        {
            timeout = 60 * 1000;
            heartbeat = Ice.ACMHeartbeat.HeartbeatOnDispatch;
            close = server ? Ice.ACMClose.CloseOnInvocation : Ice.ACMClose.CloseOnInvocationAndIdle;
        }

        public ACMConfig(Ice.Communicator communicator, Ice.Logger logger, string prefix, ACMConfig defaults)
        {
            Debug.Assert(prefix != null);

            string timeoutProperty;
            if ((prefix == "Ice.ACM.Client" || prefix == "Ice.ACM.Server") &&
                communicator.GetProperty($"{prefix}.Timeout") == null)
            {
                timeoutProperty = prefix; // Deprecated property.
            }
            else
            {
                timeoutProperty = prefix + ".Timeout";
            }

            timeout = communicator.GetPropertyAsInt(timeoutProperty) * 1000 ?? defaults.timeout;
            if (timeout < 0)
            {
                logger.warning($"invalid value for property `{timeoutProperty}', default value will be used instead");
                timeout = defaults.timeout;
            }

            int hb = communicator.GetPropertyAsInt($"{prefix}.Heartbeat") ?? (int)defaults.heartbeat;
            if (hb >= (int)Ice.ACMHeartbeat.HeartbeatOff && hb <= (int)Ice.ACMHeartbeat.HeartbeatAlways)
            {
                heartbeat = (Ice.ACMHeartbeat)hb;
            }
            else
            {
                logger.warning($"invalid value for property `{prefix}.Heartbeat', default value will be used instead");
                heartbeat = defaults.heartbeat;
            }

            int cl = communicator.GetPropertyAsInt($"{prefix}.Close") ?? (int)defaults.close;
            if (cl >= (int)Ice.ACMClose.CloseOff && cl <= (int)Ice.ACMClose.CloseOnIdleForceful)
            {
                close = (Ice.ACMClose)cl;
            }
            else
            {
                logger.warning($"invalid value for property `{prefix}.Close', default value will be used instead");
                close = defaults.close;
            }
        }

        public ACMConfig Clone()
        {
            return (ACMConfig)MemberwiseClone();
        }

        public int timeout;
        public Ice.ACMHeartbeat heartbeat;
        public Ice.ACMClose close;
    }

    public interface ACMMonitor : TimerTask
    {
        void add(Ice.ConnectionI con);
        void remove(Ice.ConnectionI con);
        void reap(Ice.ConnectionI con);

        ACMMonitor acm(int? timeout, Ice.ACMClose? c, Ice.ACMHeartbeat? h);
        Ice.ACM getACM();
    }

    public class FactoryACMMonitor : ACMMonitor
    {
        internal class Change
        {
            internal Change(Ice.ConnectionI connection, bool remove)
            {
                this.connection = connection;
                this.remove = remove;
            }

            public readonly Ice.ConnectionI connection;
            public readonly bool remove;
        }

        internal FactoryACMMonitor(Ice.Communicator communicator, ACMConfig config)
        {
            _communicator = communicator;
            _config = config;
        }

        internal void destroy()
        {
            lock (this)
            {
                if (_communicator == null)
                {
                    //
                    // Ensure all the connections have been cleared, it's important to wait here
                    // to prevent the timer destruction in IceInternal::Instance::destroy.
                    //
                    while (_connections.Count > 0)
                    {
                        System.Threading.Monitor.Wait(this);
                    }
                    return;
                }

                if (_connections.Count > 0)
                {
                    //
                    // Cancel the scheduled timer task and schedule it again now to clear the
                    // connection set from the timer thread.
                    //
                    _communicator.timer().cancel(this);
                    _communicator.timer().schedule(this, 0);
                }

                _communicator = null;
                _changes.Clear();

                //
                // Wait for the connection set to be cleared by the timer thread.
                //
                while (_connections.Count > 0)
                {
                    System.Threading.Monitor.Wait(this);
                }
            }
        }

        public void add(Ice.ConnectionI connection)
        {
            if (_config.timeout == 0)
            {
                return;
            }

            lock (this)
            {
                if (_connections.Count == 0)
                {
                    Debug.Assert(_communicator != null);
                    _connections.Add(connection);
                    _communicator.timer().scheduleRepeated(this, _config.timeout / 2);
                }
                else
                {
                    _changes.Add(new Change(connection, false));
                }
            }
        }

        public void remove(Ice.ConnectionI connection)
        {
            if (_config.timeout == 0)
            {
                return;
            }

            lock (this)
            {
                Debug.Assert(_communicator != null);
                _changes.Add(new Change(connection, true));
            }
        }

        public void reap(Ice.ConnectionI connection)
        {
            lock (this)
            {
                _reapedConnections.Add(connection);
            }
        }

        public ACMMonitor acm(int? timeout, Ice.ACMClose? c, Ice.ACMHeartbeat? h)
        {
            Debug.Assert(_communicator != null);

            ACMConfig config = _config.Clone();
            if (timeout.HasValue)
            {
                config.timeout = timeout.Value * 1000; // To milliseconds
            }
            if (c.HasValue)
            {
                config.close = c.Value;
            }
            if (h.HasValue)
            {
                config.heartbeat = h.Value;
            }
            return new ConnectionACMMonitor(this, _communicator.timer(), config);
        }

        public Ice.ACM getACM()
        {
            return new Ice.ACM
            {
                timeout = _config.timeout / 1000,
                close = _config.close,
                heartbeat = _config.heartbeat
            };
        }

        internal List<Ice.ConnectionI>? swapReapedConnections()
        {
            lock (this)
            {
                if (_reapedConnections.Count == 0)
                {
                    return null;
                }
                List<Ice.ConnectionI> connections = _reapedConnections;
                _reapedConnections = new List<Ice.ConnectionI>();
                return connections;
            }
        }

        public void runTimerTask()
        {
            lock (this)
            {
                if (_communicator == null)
                {
                    _connections.Clear();
                    System.Threading.Monitor.PulseAll(this);
                    return;
                }

                foreach (Change change in _changes)
                {
                    if (change.remove)
                    {
                        _connections.Remove(change.connection);
                    }
                    else
                    {
                        _connections.Add(change.connection);
                    }
                }
                _changes.Clear();

                if (_connections.Count == 0)
                {
                    _communicator.timer().cancel(this);
                    return;
                }
            }

            //
            // Monitor connections outside the thread synchronization, so
            // that connections can be added or removed during monitoring.
            //
            long now = Time.currentMonotonicTimeMillis();
            foreach (Ice.ConnectionI connection in _connections)
            {
                try
                {
                    connection.monitor(now, _config);
                }
                catch (System.Exception ex)
                {
                    handleException(ex);
                }
            }
        }

        internal void handleException(System.Exception ex)
        {
            lock (this)
            {
                if (_communicator == null)
                {
                    return;
                }
                _communicator.Logger.error("exception in connection monitor:\n" + ex);
            }
        }

        private Ice.Communicator? _communicator;
        private readonly ACMConfig _config;

        private readonly HashSet<Ice.ConnectionI> _connections = new HashSet<Ice.ConnectionI>();
        private readonly List<Change> _changes = new List<Change>();
        private List<Ice.ConnectionI> _reapedConnections = new List<Ice.ConnectionI>();
    }

    internal class ConnectionACMMonitor : ACMMonitor
    {
        internal ConnectionACMMonitor(FactoryACMMonitor parent, Timer timer, ACMConfig config)
        {
            _parent = parent;
            _timer = timer;
            _config = config;
        }

        public void add(Ice.ConnectionI connection)
        {
            lock (this)
            {
                Debug.Assert(_connection == null);
                _connection = connection;
                if (_config.timeout > 0)
                {
                    _timer.scheduleRepeated(this, _config.timeout / 2);
                }
            }
        }

        public void remove(Ice.ConnectionI connection)
        {
            lock (this)
            {
                Debug.Assert(_connection == connection);
                _connection = null;
                if (_config.timeout > 0)
                {
                    _timer.cancel(this);
                }
            }
        }

        public void reap(Ice.ConnectionI connection)
        {
            _parent.reap(connection);
        }

        public ACMMonitor acm(int? timeout, Ice.ACMClose? c, Ice.ACMHeartbeat? h)
        {
            return _parent.acm(timeout, c, h);
        }

        public Ice.ACM getACM()
        {
            return new Ice.ACM
            {
                timeout = _config.timeout / 1000,
                close = _config.close,
                heartbeat = _config.heartbeat
            };
        }

        public void runTimerTask()
        {
            Ice.ConnectionI connection;
            lock (this)
            {
                if (_connection == null)
                {
                    return;
                }
                connection = _connection;
            }

            try
            {
                connection.monitor(Time.currentMonotonicTimeMillis(), _config);
            }
            catch (System.Exception ex)
            {
                _parent.handleException(ex);
            }
        }

        private readonly FactoryACMMonitor _parent;
        private readonly Timer _timer;
        private readonly ACMConfig _config;

        private Ice.ConnectionI? _connection;
    }
}
