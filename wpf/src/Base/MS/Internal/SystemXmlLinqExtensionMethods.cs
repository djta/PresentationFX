//---------------------------------------------------------------------------
//
// <copyright file="SystemXmlLinqExtensionMethods.cs" company="Microsoft">
//    Copyright (C) Microsoft Corporation.  All rights reserved.
// </copyright>
//
// Description: Helper methods for code that uses types from System.Xml.Linq.
//
//---------------------------------------------------------------------------

using System;
using System.ComponentModel;

namespace MS.Internal
{
    internal abstract class SystemXmlLinqExtensionMethods
    {
        // return true if the item is an XElement
        internal abstract bool IsXElement(object item);

        // return a string of the form "{http://my.namespace}TagName"
        internal abstract string GetXElementTagName(object item);

        // XLinq exposes two synthetic properties - Elements and Descendants -
        // on XElement that return IEnumerable<XElement>.  We handle these specially
        // to work around problems involving identity and change notifications
        internal abstract bool IsXLinqCollectionProperty(PropertyDescriptor pd);

        // XLinq exposes several properties on XElement that create new objects
        // every time the getter is called.
        internal abstract bool IsXLinqNonIdempotentProperty(PropertyDescriptor pd);
    }
}
