#ifndef __PRINTSYSTEMPATHRESOLVER_HPP__
#define __PRINTSYSTEMPATHRESOLVER_HPP__

/*++
                                                                              
    Copyright (C) 2002 - 2003 Microsoft Corporation                                   
    All rights reserved.                                                        
                                                                              
    Module Name:                                                                
        PrintSystemResolver.hpp                                                             
                                                                              
    Abstract:
        
    Author:                                                                     
        Khaled Sedky (khaleds) 20-November-2002                                        
     
                                                                             
    Revision History:                                                           
--*/

namespace System
{
namespace Printing
{
    private enum class TransportProtocol
    {
        Unknown = 0,
        Unc     = 1,
        TcpIP   = 2,
        Http    = 3
    };


    private ref class PrintSystemProtocol
    {
        public:

        PrintSystemProtocol(
            TransportProtocol   transportType,
            String^             transportpath
            );

        property
        String^
        Path
        {
            String^ get();
        }

        private:
        
        TransportProtocol   transport;
        String^             path;
    };

    private interface class IPrintSystemPathResolver
    {
        PrintSystemProtocol^
        Resolve(
            PrintPropertyDictionary^ collection
            );
    };

    private ref class PrintSystemPathResolver
    {
        public:

        PrintSystemPathResolver(
            PrintPropertyDictionary^             collection,
            IPrintSystemPathResolver^            resolver
            );

        ~PrintSystemPathResolver(
            void
            );

        property
        PrintSystemProtocol^
        Protocol
        {
            PrintSystemProtocol^ get();
        }
       
        bool
        Resolve(
            void
            );

        private:
        
        PrintPropertyDictionary^             protocolParametersCollection;
        PrintSystemProtocol^                 protocol;
        IPrintSystemPathResolver^            chainLink;
    };

    private ref class PrintSystemDefaultPathResolver : 
    public IPrintSystemPathResolver
    {
        public:

	    PrintSystemDefaultPathResolver(
		    void
		    );

	    ~PrintSystemDefaultPathResolver(
		    void
		    );

        virtual
        PrintSystemProtocol^
        Resolve(
            PrintPropertyDictionary^    collection
            );

	    private:

        IPrintSystemPathResolver^       chainLink;
    };

    private ref class PrintSystemUNCPathResolver : 
    public IPrintSystemPathResolver
    {
        private:

        static 
        PrintSystemUNCPathResolver(
            void
            )
        {
            parametersMapping = gcnew Hashtable();

            parametersMapping->Add("ServerName",  
                                   gcnew ValidateAndCaptureStringParameter(&ValidateAndCaptureServerName));

            parametersMapping->Add("PrinterName", 
                                   gcnew ValidateAndCaptureStringParameter(&ValidateAndCapturePrinterName));
        }

        public:

	    PrintSystemUNCPathResolver(
		    IPrintSystemPathResolver^ resolver
		    );

	    ~PrintSystemUNCPathResolver(
		    void
		    );

        virtual
        PrintSystemProtocol^
        Resolve(
            PrintPropertyDictionary^    collection
            );

        property
        String^
        ServerName
        {
            public:
                String^ get();
            private:
                void set(String^ name);
        }

        property
        String^
        PrinterName
        {
            public:
                String^ get();
            private:
                void set(String^ name);
        }

        static
        bool
        ValidateUNCPath(
            String^ name
            );

	    private:

        delegate
        bool
        ValidateAndCaptureStringParameter(
            Object^                     parameter,
            PrintSystemUNCPathResolver^ resolver
            );

        static
        bool
        ValidateAndCaptureServerName(
            Object^                     parameter,
            PrintSystemUNCPathResolver^ resolver
            );

        static
        bool
        ValidateAndCapturePrinterName(
            Object^                     parameter,
            PrintSystemUNCPathResolver^ resolver
            );

        static
        bool
        ValidateUNCName(
            String^ name
            );

        void
        BuildUncPath(
            void
            );

        void
        ValidateCollectionAndCaptureParameters(
            IDictionaryEnumerator^ enumerator
            );

        IPrintSystemPathResolver^               chainLink;
        String^                                 serverName;
        String^                                 printerName;
        String^                                 uncPath;

        static
        Hashtable^                              parametersMapping;
    };

    private ref class PrintSystemUNCPath----er
    {
        public:

	    PrintSystemUNCPath----er(
		    String^ path
		    );

	    ~PrintSystemUNCPath----er(
		    void
		    );

        property
        String^
        PrintServerName
        {
            String^ get();
        }

        property
        String^
        PrintQueueName
        {
            String^ get();
        }

	    private:

        String^     printServerName;
        String^     printQueueName;
    };
}
}
#endif
