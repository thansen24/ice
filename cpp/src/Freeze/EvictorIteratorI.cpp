// **********************************************************************
//
// Copyright (c) 2003-2004
// ZeroC, Inc.
// Billerica, MA, USA
//
// All Rights Reserved.
//
// Ice is free software; you can redistribute it and/or modify it under
// the terms of the GNU General Public License version 2 as published by
// the Free Software Foundation.
//
// **********************************************************************

#include <Freeze/EvictorIteratorI.h>
#include <Freeze/ObjectStore.h>
#include <Freeze/EvictorI.h>
#include <Freeze/Util.h>

using namespace std;
using namespace Freeze;
using namespace Ice;


Freeze::EvictorIteratorI::EvictorIteratorI(ObjectStore* store, Int batchSize) :
    _store(store),
    _batchSize(static_cast<size_t>(batchSize)),
    _key(1024),
    _more(store != 0),
    _initialized(false)
{
    _batchIterator = _batch.end();
}


bool
Freeze::EvictorIteratorI::hasNext()
{
    if(_batchIterator != _batch.end()) 
    {
	return true;
    }
    else
    {
	_batchIterator = nextBatch();
	return (_batchIterator != _batch.end());
    }
}

Identity
Freeze::EvictorIteratorI::next()
{
    if(hasNext())
    {
	return *_batchIterator++;
    }
    else
    {
	throw Freeze::NoSuchElementException(__FILE__, __LINE__);
    }
}


vector<Identity>::const_iterator
Freeze::EvictorIteratorI::nextBatch()
{
    _batch.clear();

    if(!_more)
    {
	return _batch.end();
    }

    vector<EvictorElementPtr> evictorElements;
    evictorElements.reserve(_batchSize);
     
    Key firstKey;
    firstKey = _key;

    CommunicatorPtr communicator = _store->communicator();
   
    try
    {
	for(;;)
	{
	    _batch.clear();
	    evictorElements.clear();
	    
	    Dbt dbKey;
	    initializeOutDbt(_key, dbKey);

	    Dbt dbValue;
	    dbValue.set_flags(DB_DBT_USERMEM | DB_DBT_PARTIAL);
	   
	    Dbc* dbc = 0;
	    try
	    {
		//
		// Move to the first record
		// 
		u_int32_t flags = DB_NEXT;

		if(_initialized)
		{
		    //
		    // _key represents the next element not yet returned
		    // if it has been deleted, we want the one after
		    //
		    flags = DB_SET_RANGE;

		    //
		    // Will be used as input as well
		    //
		    dbKey.set_size(static_cast<u_int32_t>(firstKey.size()));
		}
		
		_store->db()->cursor(0, &dbc, 0);

		bool done = false;
		do
		{
		    for(;;)
		    {
			try
			{
			    _more = (dbc->get(&dbKey, &dbValue, flags) == 0);
			    if(_more)
			    {
				_key.resize(dbKey.get_size());
				_initialized = true;

				flags = DB_NEXT;
		    
				Ice::Identity ident;
				ObjectStore::unmarshal(ident, _key, communicator);
				if(_batch.size() < _batchSize)
				{
				    _batch.push_back(ident);
				}
				else
				{
				    //
				    // Keep the last element in _key
				    //
				    done = true;
				}
			    }
			    break;
			}
			catch(const DbMemoryException& dx)
			{
			    handleMemoryException(dx, _key, dbKey);
			}
		    }
		}
		while(!done && _more);

		Dbc* toClose = dbc;
		dbc = 0;
		toClose->close();
		break; // for (;;)
	    }
	    catch(const DbDeadlockException&)
	    {
		if(dbc != 0)
		{
		    try
		    {
			dbc->close();
		    }
		    catch(const DbDeadlockException&)
		    {
			//
			// Ignored
			//
		    }
		}
		_key = firstKey;
		//
		// Retry
		//
	    }
	    catch(...)
	    {
		if(dbc != 0)
		{
		    try
		    {
			dbc->close();
		    }
		    catch(const DbDeadlockException&)
		    {
			//
			// Ignored
			//
		    }
		}
		throw;
	    }
	}
    }
    catch(const DbException& dx)
    {
	DatabaseException ex(__FILE__, __LINE__);
	ex.message = dx.what();
	throw ex;
    }
    
    if(_batch.size() == 0)
    {
	return _batch.end();
    }
    else
    {
	return _batch.begin();
    }
}
