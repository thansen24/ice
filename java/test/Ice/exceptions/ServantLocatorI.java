// **********************************************************************
//
// Copyright (c) 2002
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

// Ice version 0.0.1

public final class ServantLocatorI extends Ice.LocalObjectImpl implements Ice.ServantLocator
{
    public Ice.Object locate(Ice.Current curr, Ice.LocalObjectHolder cookie)
    {
        return null;
    }

    public void finished(Ice.Current curr, Ice.Object servant, Ice.LocalObject cookie)
    {
    }

    public void deactivate()
    {
    }
}
