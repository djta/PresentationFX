//------------------------------------------------------------------------------
// <copyright file="etwprovider.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// <OWNER>[....]</OWNER>
//------------------------------------------------------------------------------
using Microsoft.Win32;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Permissions;
using System.Threading;
using System;

namespace System.Diagnostics.Tracing
{
    // New in CLR4.0
    internal enum ControllerCommand
    {
        // Strictly Positive numbers are for provider-specific commands, negative number are for 'shared' commands. 256
        // The first 256 negative numbers are reserved for the framework.  
        Update = 0,                 // Not used by EventPrividerBase.  
        SendManifest = -1,
        Enable = -2,
        Disable = -3,
    };

    /// <summary>
    /// Only here because System.Diagnostics.EventProvider needs one more extensibility hook (when it gets a 
    /// controller callback)
    /// </summary>
    [System.Security.Permissions.HostProtection(MayLeakOnAbort = true)]
    internal class EventProvider : IDisposable
    {
        // This is the windows EVENT_DATA_DESCRIPTOR structure.  We expose it because this is what
        // subclasses of EventProvider use when creating efficient (but unsafe) version of
        // EventWrite.   We do make it a nested type because we really don't expect anyone to use 
        // it except subclasses (and then only rarely).  
        public struct EventData
        {
            internal unsafe ulong Ptr;
            internal uint Size;
            internal uint Reserved;
        }

        /// <summary>
        /// A struct characterizing ETW sessions (identified by the etwSessionId) as
        /// activity-tracing-aware or legacy. A session that's activity-tracing-aware
        /// has specified one non-zero bit in the reserved range 44-47 in the 
        /// 'allKeywords' value it passed in for a specific EventProvider.
        /// </summary>
        public struct SessionInfo
        {
            internal int sessionIdBit;      // the index of the bit used for tracing in the "reserved" field of AllKeywords
            internal int etwSessionId;      // the machine-wide ETW session ID

            internal SessionInfo(int sessionIdBit_, int etwSessionId_)
            { sessionIdBit = sessionIdBit_; etwSessionId = etwSessionId_; }
        }

        [SecurityCritical]
        UnsafeNativeMethodsX.ManifestEtw.EtwEnableCallback m_etwCallback;     // Trace Callback function
        private long m_regHandle;                        // Trace Registration Handle
        private byte m_level;                            // Tracing Level
        private long m_anyKeywordMask;                   // Trace Enable Flags
        private long m_allKeywordMask;                   // Match all keyword
        private List<SessionInfo> m_liveSessions;        // current live sessions (Tuple<sessionIdBit, etwSessionId>)
        private bool m_enabled;                          // Enabled flag from Trace callback
        private Guid m_providerId;                       // Control Guid 
        private int m_disposed;                          // when 1, provider has unregister

        [ThreadStatic]
        private static WriteEventErrorCode s_returnCode; // The last return code 

        private const int s_basicTypeAllocationBufferSize = 16;
        private const int s_etwMaxMumberArguments = 32;
        private const int s_etwAPIMaxStringCount = 8;
        private const int s_maxEventDataDescriptors = 128;
        private const int s_traceEventMaximumSize = 65482;
        private const int s_traceEventMaximumStringSize = 32724;

        [SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible")]
        public enum WriteEventErrorCode : int
        {
            //check mapping to runtime codes
            NoError = 0,
            NoFreeBuffers = 1,
            EventTooBig = 2,
            NullInput = 3,
            TooManyArgs = 4,
            Other = 5, 
        };

        // <SecurityKernel Critical="True" Ring="1">
        // <ReferencesCritical Name="Method: Register():Void" Ring="1" />
        // </SecurityKernel>
        /// <summary>
        /// Constructs a new EventProvider.  This causes the class to be registered with the OS and
        /// if an ETW controller turns on the logging then logging will start. 
        /// </summary>
        /// <param name="providerGuid">The GUID that identifies this provider to the system.</param>
        [System.Security.SecurityCritical]
#pragma warning disable 618
        [PermissionSet(SecurityAction.Demand, Unrestricted = true)]
#pragma warning restore 618
        [SuppressMessage("Microsoft.Naming", "CA1720:IdentifiersShouldNotContainTypeNames", MessageId = "guid")]
        protected EventProvider(Guid providerGuid)
        {
            m_providerId = providerGuid;
            //
            // Register the ProviderId with ETW
            //
            Register(providerGuid);
        }

        internal EventProvider()
        {
        }

        /// <summary>
        /// This method registers the controlGuid of this class with ETW. We need to be running on
        /// Vista or above. If not a PlatformNotSupported exception will be thrown. If for some 
        /// reason the ETW Register call failed a NotSupported exception will be thrown. 
        /// </summary>
        // <SecurityKernel Critical="True" Ring="0">
        // <CallsSuppressUnmanagedCode Name="UnsafeNativeMethods.ManifestEtw.EventRegister(System.Guid&,Microsoft.Win32.UnsafeNativeMethods.ManifestEtw+EtwEnableCallback,System.Void*,System.Int64&):System.UInt32" />
        // <SatisfiesLinkDemand Name="Win32Exception..ctor(System.Int32)" />
        // <ReferencesCritical Name="Method: EtwEnableCallBack(Guid&, Int32, Byte, Int64, Int64, Void*, Void*):Void" Ring="1" />
        // </SecurityKernel>
        [System.Security.SecurityCritical]
        internal unsafe void Register(Guid providerGuid)
        {
            m_providerId = providerGuid;
            uint status;
            m_etwCallback = new UnsafeNativeMethodsX.ManifestEtw.EtwEnableCallback(EtwEnableCallBack);

            status = EventRegister(ref m_providerId, m_etwCallback); 
            if (status != 0)
            {
                throw new ArgumentException(Win32Native.GetMessage(unchecked((int)status)));
            }
        }

        //
        // implement Dispose Pattern to early deregister from ETW insted of waiting for 
        // the finalizer to call deregistration.
        // Once the user is done with the provider it needs to call Close() or Dispose()
        // If neither are called the finalizer will unregister the provider anyway
        //
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // <SecurityKernel Critical="True" TreatAsSafe="Does not expose critical resource" Ring="1">
        // <ReferencesCritical Name="Method: Deregister():Void" Ring="1" />
        // </SecurityKernel>
        [System.Security.SecuritySafeCritical]
        protected virtual void Dispose(bool disposing)
        {
            //
            // explicit cleanup is done by calling Dispose with true from 
            // Dispose() or Close(). The disposing arguement is ignored because there
            // are no unmanaged resources.
            // The finalizer calls Dispose with false.
            //

            //
            // check if the object has been allready disposed
            //
            if (m_disposed == 1) return;

            if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            {
                // somebody is allready disposing the provider
                return;
            }

            //
            // Disables Tracing in the provider, then unregister
            // 

            m_enabled = false;

            Deregister();
        }

        /// <summary>
        /// This method deregisters the controlGuid of this class with ETW.
        /// 
        /// </summary>
        public virtual void Close()
        {
            Dispose();
        }

        ~EventProvider()
        {
            Dispose(false);
        }

        /// <summary>
        /// This method un-registers from ETW.
        /// </summary>
        // <SecurityKernel Critical="True" Ring="0">
        // <CallsSuppressUnmanagedCode Name="UnsafeNativeMethods.ManifestEtw.EventUnregister(System.Int64):System.Int32" />
        // </SecurityKernel>
        // 
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA1806:DoNotIgnoreMethodResults", MessageId = "Microsoft.Win32.UnsafeNativeMethods.ManifestEtw.EventUnregister(System.Int64)"), System.Security.SecurityCritical]
        private unsafe void Deregister()
        {
            //
            // Unregister from ETW using the RegHandle saved from
            // the register call.
            //

            if (m_regHandle != 0)
            {
                EventUnregister();
                m_regHandle = 0;
            }
        }

        // <SecurityKernel Critical="True" Ring="0">
        // <UsesUnsafeCode Name="Parameter filterData of type: Void*" />
        // <UsesUnsafeCode Name="Parameter callbackContext of type: Void*" />
        // </SecurityKernel>
        [System.Security.SecurityCritical]
        unsafe void EtwEnableCallBack(
                        [In] ref System.Guid sourceId,
                        [In] int controlCode,
                        [In] byte setLevel,
                        [In] long anyKeyword,
                        [In] long allKeyword,
                        [In] UnsafeNativeMethodsX.ManifestEtw.EVENT_FILTER_DESCRIPTOR* filterData,
                        [In] void* callbackContext
                        )
        {
            ControllerCommand command = ControllerCommand.Update;
            IDictionary<string, string> args = null;
            byte[] data;
            int keyIndex;
            bool skipFinalOnControllerCommand = false;
            EventSource.OutputDebugString(string.Format("EtwEnableCallBack(ctrl {0}, lvl {1}, any {2:x}, all {3:x})", 
                                          controlCode, setLevel, anyKeyword, allKeyword));
            if (controlCode == UnsafeNativeMethodsX.ManifestEtw.EVENT_CONTROL_CODE_ENABLE_PROVIDER)
            {
                m_enabled = true;
                m_level = setLevel;
                m_anyKeywordMask = anyKeyword;
                m_allKeywordMask = allKeyword;

                List<Tuple<SessionInfo, bool>> sessionsChanged = GetSessions();
                foreach (var session in sessionsChanged)
                {
                    int sessionChanged = session.Item1.sessionIdBit;
                    int etwSessionId = session.Item1.etwSessionId;
                    bool bEnabling = session.Item2;

                    EventSource.OutputDebugString(string.Format(CultureInfo.InvariantCulture, "EtwEnableCallBack: session changed {0}:{1}:{2}", 
                        sessionChanged, etwSessionId, bEnabling));

                    skipFinalOnControllerCommand = true;
                    args = null;                                // reinitialize args for every session...

                    // if we get more than one session changed we have no way
                    // of knowing which one "filterData" belongs to
                    if (sessionsChanged.Count > 1)
                        filterData = null;

                    // read filter data only when a session is being *added*
                    if (bEnabling && 
                        GetDataFromController(etwSessionId, filterData, out command, out data, out keyIndex))
                    {
                        args = new Dictionary<string, string>(4);
                        while (keyIndex < data.Length)
                        {
                            int keyEnd = FindNull(data, keyIndex);
                            int valueIdx = keyEnd + 1;
                            int valueEnd = FindNull(data, valueIdx);
                            if (valueEnd < data.Length)
                            {
                                string key = System.Text.Encoding.UTF8.GetString(data, keyIndex, keyEnd - keyIndex);
                                string value = System.Text.Encoding.UTF8.GetString(data, valueIdx, valueEnd - valueIdx);
                                args[key] = value;
                            }
                            keyIndex = valueEnd + 1;
                        }
                    }

                    // execute OnControllerCommand once for every session that has changed.
                    try
                    {
                        OnControllerCommand(command, args, (bEnabling ? sessionChanged : -sessionChanged), etwSessionId);
                    }
                    catch (Exception)
                    {
                        // We want to ignore any failures that happen as a result of turning on this provider as to
                        // not crash the app.
                    }
                }
            }
            else if (controlCode == UnsafeNativeMethodsX.ManifestEtw.EVENT_CONTROL_CODE_DISABLE_PROVIDER)
            {
                m_enabled = false;
                m_level = 0;
                m_anyKeywordMask = 0;
                m_allKeywordMask = 0;
                m_liveSessions = null;
            }
            else if (controlCode == UnsafeNativeMethodsX.ManifestEtw.EVENT_CONTROL_CODE_CAPTURE_STATE)
            {
                command = ControllerCommand.SendManifest;
            }
            else
                return;     // per spec you ignore commands you don't recognise.  

            try
            {
                if (!skipFinalOnControllerCommand)
                    OnControllerCommand(command, args, 0, 0);
            }
            catch (Exception)
            {
                // We want to ignore any failures that happen as a result of turning on this provider as to
                // not crash the app.
            }
        }

        // New in CLR4.0
        protected virtual void OnControllerCommand(ControllerCommand command, IDictionary<string, string> arguments, int sessionId, int etwSessionId) { }
        protected EventLevel Level { get { return (EventLevel)m_level; } set { m_level = (byte)value; } }
        protected EventKeywords MatchAnyKeyword { get { return (EventKeywords)m_anyKeywordMask; } set { m_anyKeywordMask = (long)value; } }
        protected EventKeywords MatchAllKeyword { get { return (EventKeywords)m_allKeywordMask; } set { m_allKeywordMask = (long)value; } }

        static private int FindNull(byte[] buffer, int idx)
        {
            while (idx < buffer.Length && buffer[idx] != 0)
                idx++;
            return idx;
        }

        /// <summary>
        /// Determines the ETW sessions that have been added and/or removed to the set of
        /// sessions interested in the current provider. It does so by (1) enumerating over all
        /// ETW sessions that enabled 'this.m_Guid' for the current process ID, and (2)
        /// comparing the current list with a list it cached on the previous invocation.
        ///
        /// The return value is a list of tuples, where the SessionInfo specifies the
        /// ETW session that was added or remove, and the bool specifies whether the
        /// session was added or whether it was removed from the set.
        /// </summary>
        [System.Security.SecuritySafeCritical]
        private List<Tuple<SessionInfo, bool>> GetSessions()
        {
            List<SessionInfo> liveSessionList = null;

            GetSessionInfo((Action<int, long>)
                ((etwSessionId, matchAllKeywords) => 
                    GetSessionInfoCallback(etwSessionId, matchAllKeywords, ref liveSessionList)));

            List<Tuple<SessionInfo, bool>> changedSessionList = new List<Tuple<SessionInfo, bool>>();

            // first look for sessions that have gone away (or have changed)
            // (present in the m_liveSessions but not in the new liveSessionList)
            if (m_liveSessions != null)
            {
                foreach(SessionInfo s in m_liveSessions)
                {
                    int idx;
                    if ((idx = IndexOfSessionInList(liveSessionList, s.etwSessionId)) < 0 ||
                        (liveSessionList[idx].sessionIdBit != s.sessionIdBit))
                        changedSessionList.Add(Tuple.Create(s, false));
                        
                }
            }
            // next look for sessions that were created since the last callback  (or have changed)
            // (present in the new liveSessionList but not in m_liveSessions)
            if (liveSessionList != null)
            {
                foreach (SessionInfo s in liveSessionList)
                {
                    int idx;
                    if ((idx = IndexOfSessionInList(m_liveSessions, s.etwSessionId)) < 0 ||
                        (m_liveSessions[idx].sessionIdBit != s.sessionIdBit))
                        changedSessionList.Add(Tuple.Create(s, true));
                }
            }

            m_liveSessions = liveSessionList;

            return changedSessionList;
        }


        /// <summary>
        /// This method is the callback used by GetSessions() when it calls into GetSessionInfo(). 
        /// It updates a List<SessionInfo> based on the etwSessionId and matchAllKeywords that 
        /// GetSessionInfo() passes in.
        /// </summary>
        private static void GetSessionInfoCallback(int etwSessionId, long matchAllKeywords,
                                ref List<SessionInfo> sessionList)
        {
            uint sessionIdBitMask = (uint)SessionMask.FromEventKeywords((ulong)matchAllKeywords);
            // an ETW controller that specifies more than the mandated bit for our EventSource
            // will be ignored...
            if (bitcount(sessionIdBitMask) > 1)
                return;

            if (sessionList == null)
                sessionList = new List<SessionInfo>(8);

            if (bitcount(sessionIdBitMask) == 1)
            {
                // activity-tracing-aware etw session
                sessionList.Add(new SessionInfo(bitindex(sessionIdBitMask)+1, etwSessionId));
            }
            else
            {
                // legacy etw session
                sessionList.Add(new SessionInfo(bitcount((uint)SessionMask.All)+1, etwSessionId));
            }
        }

        /// <summary>
        /// This method enumerates over all active ETW sessions that have enabled 'this.m_Guid' 
        /// for the current process ID, calling 'action' for each session, and passing it the
        /// ETW session and the 'AllKeywords' the session enabled for the current provider.
        /// </summary>
        [System.Security.SecurityCritical]
        private unsafe void GetSessionInfo(Action<int, long> action)
        {
            int buffSize = 256;     // An initial guess that probably works most of the time.  
            byte* buffer;
            for (; ; )
            {
                var space = stackalloc byte[buffSize];
                buffer = space;
                var hr = 0;

                fixed (Guid* provider = &m_providerId)
                {
                    hr = UnsafeNativeMethodsX.ManifestEtw.EnumerateTraceGuidsEx(UnsafeNativeMethodsX.ManifestEtw.TRACE_QUERY_INFO_CLASS.TraceGuidQueryInfo,
                        provider, sizeof(Guid), buffer, buffSize, ref buffSize);
                }
                if (hr == 0)
                    break;
                if (hr != 122 /* ERROR_INSUFFICIENT_BUFFER */)
                    return;
            }

            var providerInfos = (UnsafeNativeMethodsX.ManifestEtw.TRACE_GUID_INFO*)buffer;
            var providerInstance = (UnsafeNativeMethodsX.ManifestEtw.TRACE_PROVIDER_INSTANCE_INFO*)&providerInfos[1];
            int processId = (int)Win32Native.GetCurrentProcessId();
            // iterate over the instances of the EventProvider in all processes
            for (int i = 0; i < providerInfos->InstanceCount; i++)
            {
                if (providerInstance->Pid == processId)
                {
                    var enabledInfos = (UnsafeNativeMethodsX.ManifestEtw.TRACE_ENABLE_INFO*)&providerInstance[1];
                    // iterate over the list of active ETW sessions "listening" to the current provider
                    for (int j = 0; j < providerInstance->EnableCount; j++)
                        action(enabledInfos[j].LoggerId, enabledInfos[j].MatchAllKeyword);
                }
                if (providerInstance->NextOffset == 0)
                    break;
                Contract.Assert(0 <= providerInstance->NextOffset && providerInstance->NextOffset < buffSize);
                var structBase = (byte*)providerInstance;
                providerInstance = (UnsafeNativeMethodsX.ManifestEtw.TRACE_PROVIDER_INSTANCE_INFO*)&structBase[providerInstance->NextOffset];
            }
        }

        /// <summary>
        /// Returns the index of the SesisonInfo from 'sessions' that has the specified 'etwSessionId'
        /// or -1 if the value is not present.
        /// <summary>
        private static int IndexOfSessionInList(List<SessionInfo> sessions, int etwSessionId)
        {
            if (sessions == null)
                return -1;
            // for non-coreclr code we could use List<T>.FindIndex(Predicate<T>), but we need this to compile
            // on coreclr as well
            for (int i = 0; i < sessions.Count; ++i)
                if (sessions[i].etwSessionId == etwSessionId)
                    return i;

            return -1;    
        }

        /// <summary>
        /// Gets any data to be passed from the controller to the provider.  It starts with what is passed
        /// into the callback, but unfortunately this data is only present for when the provider is active
        /// at the the time the controller issues the command.  To allow for providers to activate after the
        /// controller issued a command, we also check the registry and use that to get the data.  The function
        /// returns an array of bytes representing the data, the index into that byte array where the data
        /// starts, and the command being issued associated with that data.  
        /// </summary>
        [System.Security.SecurityCritical]
        private unsafe bool GetDataFromController(int etwSessionId, 
                UnsafeNativeMethodsX.ManifestEtw.EVENT_FILTER_DESCRIPTOR* filterData, out ControllerCommand command, out byte[] data, out int dataStart)
        {
            data = null;
            dataStart = 0;
            if (filterData == null)
            {
                string regKey = @"\Microsoft\Windows\CurrentVersion\Winevt\Publishers\{" + m_providerId + "}";
                if (System.Runtime.InteropServices.Marshal.SizeOf(typeof(IntPtr)) == 8)
                    regKey = @"HKEY_LOCAL_MACHINE\Software" + @"\Wow6432Node" + regKey;
                else
                    regKey = @"HKEY_LOCAL_MACHINE\Software" + regKey;

                string valueName = "ControllerData_Session_" + etwSessionId.ToString(CultureInfo.InvariantCulture);

                data = Microsoft.Win32.Registry.GetValue(regKey, valueName, null) as byte[];
                if (data != null)
                {
                    // We only used the persisted data from the registry for updates.   
                    command = ControllerCommand.Update;
                    return true;
                }
            }
            else
            {
                if (filterData->Ptr != 0 && 0 < filterData->Size && filterData->Size <= 1024)
                {
                    data = new byte[filterData->Size];
                    Marshal.Copy((IntPtr)filterData->Ptr, data, 0, data.Length);
                }
                command = (ControllerCommand) filterData->Type;
                return true;
            }

            command = ControllerCommand.Update;
            return false;
        }

        /// <summary>
        /// IsEnabled, method used to test if provider is enabled
        /// </summary>
        public bool IsEnabled()
        {
            return m_enabled;
        }

        /// <summary>
        /// IsEnabled, method used to test if event is enabled
        /// </summary>
        /// <param name="Lvl">
        /// Level  to test
        /// </param>
        /// <param name="Keyword">
        /// Keyword  to test
        /// </param>
        public bool IsEnabled(byte level, long keywords)
        {
            //
            // If not enabled at all, return false.
            //
            if (!m_enabled)
            {
                return false;
            }

            // This also covers the case of Level == 0.
            if ((level <= m_level) ||
                (m_level == 0))
            {

                //
                // Check if Keyword is enabled
                //

                if ((keywords == 0) ||
                    (((keywords & m_anyKeywordMask) != 0) &&
                     ((keywords & m_allKeywordMask) == m_allKeywordMask)))
                {
                    return true;
                }
            }

            return false;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        public static WriteEventErrorCode GetLastWriteEventError()
        {
            return s_returnCode;
        }

        //
        // Helper function to set the last error on the thread
        //
        private static void SetLastError(int error)
        {
            switch (error)
            {
                case UnsafeNativeMethodsX.ManifestEtw.ERROR_ARITHMETIC_OVERFLOW:
                case UnsafeNativeMethodsX.ManifestEtw.ERROR_MORE_DATA:
                    s_returnCode = WriteEventErrorCode.EventTooBig;
                    break;
                case UnsafeNativeMethodsX.ManifestEtw.ERROR_NOT_ENOUGH_MEMORY:
                    s_returnCode = WriteEventErrorCode.NoFreeBuffers;
                    break;
            }
        }

        // <SecurityKernel Critical="True" Ring="0">
        // <UsesUnsafeCode Name="Local intptrPtr of type: IntPtr*" />
        // <UsesUnsafeCode Name="Local intptrPtr of type: Int32*" />
        // <UsesUnsafeCode Name="Local longptr of type: Int64*" />
        // <UsesUnsafeCode Name="Local uintptr of type: UInt32*" />
        // <UsesUnsafeCode Name="Local ulongptr of type: UInt64*" />
        // <UsesUnsafeCode Name="Local charptr of type: Char*" />
        // <UsesUnsafeCode Name="Local byteptr of type: Byte*" />
        // <UsesUnsafeCode Name="Local shortptr of type: Int16*" />
        // <UsesUnsafeCode Name="Local sbyteptr of type: SByte*" />
        // <UsesUnsafeCode Name="Local ushortptr of type: UInt16*" />
        // <UsesUnsafeCode Name="Local floatptr of type: Single*" />
        // <UsesUnsafeCode Name="Local doubleptr of type: Double*" />
        // <UsesUnsafeCode Name="Local boolptr of type: Boolean*" />
        // <UsesUnsafeCode Name="Local guidptr of type: Guid*" />
        // <UsesUnsafeCode Name="Local decimalptr of type: Decimal*" />
        // <UsesUnsafeCode Name="Local booleanptr of type: Boolean*" />
        // <UsesUnsafeCode Name="Parameter dataDescriptor of type: EventData*" />
        // <UsesUnsafeCode Name="Parameter dataBuffer of type: Byte*" />
        // </SecurityKernel>
        [System.Security.SecurityCritical]
        private static unsafe string EncodeObject(ref object data, EventData* dataDescriptor, byte* dataBuffer)
        /*++

        Routine Description:

           This routine is used by WriteEvent to unbox the object type and
           to fill the passed in ETW data descriptor. 

        Arguments:

           data - argument to be decoded

           dataDescriptor - pointer to the descriptor to be filled

           dataBuffer - storage buffer for storing user data, needed because cant get the address of the object

        Return Value:

           null if the object is a basic type other than string. String otherwise

        --*/
        {
            Again:
            dataDescriptor->Reserved = 0;

            string sRet = data as string;
            if (sRet != null)
            {
                dataDescriptor->Size = (uint)((sRet.Length + 1) * 2);
                return sRet;
            }

            if (data is IntPtr)
            {
                dataDescriptor->Size = (uint)sizeof(IntPtr);
                IntPtr* intptrPtr = (IntPtr*)dataBuffer;
                *intptrPtr = (IntPtr)data;
                dataDescriptor->Ptr = (ulong)intptrPtr;
            }
            else if (data is int)
            {
                dataDescriptor->Size = (uint)sizeof(int);
                int* intptr = (int*)dataBuffer;
                *intptr = (int)data;
                dataDescriptor->Ptr = (ulong)intptr;
            }
            else if (data is long)
            {
                dataDescriptor->Size = (uint)sizeof(long);
                long* longptr = (long*)dataBuffer;
                *longptr = (long)data;
                dataDescriptor->Ptr = (ulong)longptr;
            }
            else if (data is uint)
            {
                dataDescriptor->Size = (uint)sizeof(uint);
                uint* uintptr = (uint*)dataBuffer;
                *uintptr = (uint)data;
                dataDescriptor->Ptr = (ulong)uintptr;
            }
            else if (data is UInt64)
            {
                dataDescriptor->Size = (uint)sizeof(ulong);
                UInt64* ulongptr = (ulong*)dataBuffer;
                *ulongptr = (ulong)data;
                dataDescriptor->Ptr = (ulong)ulongptr;
            }
            else if (data is char)
            {
                dataDescriptor->Size = (uint)sizeof(char);
                char* charptr = (char*)dataBuffer;
                *charptr = (char)data;
                dataDescriptor->Ptr = (ulong)charptr;
            }
            else if (data is byte)
            {
                dataDescriptor->Size = (uint)sizeof(byte);
                byte* byteptr = (byte*)dataBuffer;
                *byteptr = (byte)data;
                dataDescriptor->Ptr = (ulong)byteptr;
            }
            else if (data is short)
            {
                dataDescriptor->Size = (uint)sizeof(short);
                short* shortptr = (short*)dataBuffer;
                *shortptr = (short)data;
                dataDescriptor->Ptr = (ulong)shortptr;
            }
            else if (data is sbyte)
            {
                dataDescriptor->Size = (uint)sizeof(sbyte);
                sbyte* sbyteptr = (sbyte*)dataBuffer;
                *sbyteptr = (sbyte)data;
                dataDescriptor->Ptr = (ulong)sbyteptr;
            }
            else if (data is ushort)
            {
                dataDescriptor->Size = (uint)sizeof(ushort);
                ushort* ushortptr = (ushort*)dataBuffer;
                *ushortptr = (ushort)data;
                dataDescriptor->Ptr = (ulong)ushortptr;
            }
            else if (data is float)
            {
                dataDescriptor->Size = (uint)sizeof(float);
                float* floatptr = (float*)dataBuffer;
                *floatptr = (float)data;
                dataDescriptor->Ptr = (ulong)floatptr;
            }
            else if (data is double)
            {
                dataDescriptor->Size = (uint)sizeof(double);
                double* doubleptr = (double*)dataBuffer;
                *doubleptr = (double)data;
                dataDescriptor->Ptr = (ulong)doubleptr;
            }
            else if (data is bool)
            {
                // WIN32 Bool is 4 bytes
                dataDescriptor->Size = 4;
                int* intptr = (int*)dataBuffer;
                if (((bool)data))
                {
                    *intptr = 1;
                }
                else
                {
                    *intptr = 0;
                }
                dataDescriptor->Ptr = (ulong)intptr;
            }
            else if (data is Guid)
            {
                dataDescriptor->Size = (uint)sizeof(Guid);
                Guid* guidptr = (Guid*)dataBuffer;
                *guidptr = (Guid)data;
                dataDescriptor->Ptr = (ulong)guidptr;
            }
            else if (data is decimal)
            {
                dataDescriptor->Size = (uint)sizeof(decimal);
                decimal* decimalptr = (decimal*)dataBuffer;
                *decimalptr = (decimal)data;
                dataDescriptor->Ptr = (ulong)decimalptr;
            }
            else if (data is DateTime)
            {
                long dateTimeTicks = ((DateTime)data).ToFileTimeUtc();
                dataDescriptor->Size = (uint)sizeof(long);
                long* longptr = (long*)dataBuffer;
                *longptr = dateTimeTicks;
                dataDescriptor->Ptr = (ulong)longptr;
            }
            else
            {
                if (data is System.Enum)
                {
                    Type underlyingType = Enum.GetUnderlyingType(data.GetType());
                    if (underlyingType == typeof(int))
                    {
                        data = ((IConvertible)data).ToInt32(null);
                        goto Again;
                    }
                    else if (underlyingType == typeof(long))
                    {
                        data = ((IConvertible)data).ToInt64(null);
                        goto Again;
                    }
                }

                //To our eyes, everything else is a just a string
                if (data == null)
                    sRet = "";
                else
                    sRet = data.ToString();
                dataDescriptor->Size = (uint)((sRet.Length + 1) * 2);
                return sRet;
            }

            return null;
        }

        /// <summary>
        /// WriteEvent, method to write a parameters with event schema properties
        /// </summary>
        /// <param name="EventDescriptor">
        /// Event Descriptor for this event. 
        /// </param>
        // <SecurityKernel Critical="True" Ring="0">
        // <CallsSuppressUnmanagedCode Name="UnsafeNativeMethods.ManifestEtw.EventWrite(System.Int64,EventDescriptor&,System.UInt32,System.Void*):System.UInt32" />
        // <UsesUnsafeCode Name="Local dataBuffer of type: Byte*" />
        // <UsesUnsafeCode Name="Local pdata of type: Char*" />
        // <UsesUnsafeCode Name="Local userData of type: EventData*" />
        // <UsesUnsafeCode Name="Local userDataPtr of type: EventData*" />
        // <UsesUnsafeCode Name="Local currentBuffer of type: Byte*" />
        // <UsesUnsafeCode Name="Local v0 of type: Char*" />
        // <UsesUnsafeCode Name="Local v1 of type: Char*" />
        // <UsesUnsafeCode Name="Local v2 of type: Char*" />
        // <UsesUnsafeCode Name="Local v3 of type: Char*" />
        // <UsesUnsafeCode Name="Local v4 of type: Char*" />
        // <UsesUnsafeCode Name="Local v5 of type: Char*" />
        // <UsesUnsafeCode Name="Local v6 of type: Char*" />
        // <UsesUnsafeCode Name="Local v7 of type: Char*" />
        // <ReferencesCritical Name="Method: EncodeObject(Object&, EventData*, Byte*):String" Ring="1" />
        // </SecurityKernel>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Justification = "Performance-critical code")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1045:DoNotPassTypesByReference")]
        [System.Security.SecurityCritical]
        internal unsafe bool WriteEvent(ref EventDescriptor eventDescriptor, Guid* childActivityID, params object[] eventPayload)
        {
            int status = 0;

            if (IsEnabled(eventDescriptor.Level, eventDescriptor.Keywords))
            {
                int argCount = 0;
                unsafe
                {
                    argCount = eventPayload.Length;

                    if (argCount > s_etwMaxMumberArguments)
                    {
                        s_returnCode = WriteEventErrorCode.TooManyArgs;
                        return false;
                    }

                    uint totalEventSize = 0;
                    int index;
                    int stringIndex = 0;
                    List<int> stringPosition = new List<int>(s_etwAPIMaxStringCount);
                    List<string> dataString = new List<string>(s_etwAPIMaxStringCount);
                    EventData* userData = stackalloc EventData[argCount];
                    EventData* userDataPtr = (EventData*)userData;
                    byte* dataBuffer = stackalloc byte[s_basicTypeAllocationBufferSize * argCount]; // Assume 16 chars for non-string argument
                    byte* currentBuffer = dataBuffer;

                    //
                    // The loop below goes through all the arguments and fills in the data 
                    // descriptors. For strings save the location in the dataString array.
                    // Calculates the total size of the event by adding the data descriptor
                    // size value set in EncodeObject method.
                    //
                    for (index = 0; index < eventPayload.Length; index++)
                    {
                        if (eventPayload[index] != null)
                        {
                            string isString;
                            isString = EncodeObject(ref eventPayload[index], userDataPtr, currentBuffer);
                            currentBuffer += s_basicTypeAllocationBufferSize;
                            totalEventSize += userDataPtr->Size;
                            userDataPtr++;
                            if (isString != null)
                            {
                                dataString.Add(isString);
                                stringPosition.Add(index);
                                stringIndex++;
                            }
                        }
                        else
                        {
                            s_returnCode = WriteEventErrorCode.NullInput;
                            return false;
                        }
                    }

                    if (totalEventSize > s_traceEventMaximumSize)
                    {
                        s_returnCode = WriteEventErrorCode.EventTooBig;
                        return false;
                    }

                    if (stringIndex < s_etwAPIMaxStringCount)
                    {
                        // Fast path: at most 8 string arguments

                        // ensure we have at least s_etwAPIMaxStringCount in dataString, so that
                        // the "fixed" statement below works
                        while (stringIndex < s_etwAPIMaxStringCount)
                        {
                            dataString.Add(null);
                            ++stringIndex;
                        }
                        
                        //
                        // now fix any string arguments and set the pointer on the data descriptor 
                        //
                        fixed (char* v0 = dataString[0], v1 = dataString[1], v2 = dataString[2], v3 = dataString[3],
                                v4 = dataString[4], v5 = dataString[5], v6 = dataString[6], v7 = dataString[7])
                        {
                            userDataPtr = (EventData*)userData;
                            if (dataString[0] != null)
                            {
                                userDataPtr[stringPosition[0]].Ptr = (ulong)v0;
                            }
                            if (dataString[1] != null)
                            {
                                userDataPtr[stringPosition[1]].Ptr = (ulong)v1;
                            }
                            if (dataString[2] != null)
                            {
                                userDataPtr[stringPosition[2]].Ptr = (ulong)v2;
                            }
                            if (dataString[3] != null)
                            {
                                userDataPtr[stringPosition[3]].Ptr = (ulong)v3;
                            }
                            if (dataString[4] != null)
                            {
                                userDataPtr[stringPosition[4]].Ptr = (ulong)v4;
                            }
                            if (dataString[5] != null)
                            {
                                userDataPtr[stringPosition[5]].Ptr = (ulong)v5;
                            }
                            if (dataString[6] != null)
                            {
                                userDataPtr[stringPosition[6]].Ptr = (ulong)v6;
                            }
                            if (dataString[7] != null)
                            {
                                userDataPtr[stringPosition[7]].Ptr = (ulong)v7;
                            }

                            if (childActivityID == null)
                                status = UnsafeNativeMethodsX.ManifestEtw.EventWrite(m_regHandle, ref eventDescriptor, argCount, userData);
                            else
                                status = UnsafeNativeMethodsX.ManifestEtw.EventWriteTransfer(m_regHandle, ref eventDescriptor, null, childActivityID, argCount, userData);
                        }
                    }
                    else
                    {
                        // Slow path: use pinned handles
                        userDataPtr = (EventData*)userData;

                        GCHandle[] rgGCHandle = new GCHandle[stringIndex];
                        for (int i = 0; i < stringIndex; ++i)
                        {
                            rgGCHandle[i] = GCHandle.Alloc(dataString[i], GCHandleType.Pinned);
                            fixed (char* p = dataString[i])
                                userDataPtr[stringPosition[i]].Ptr = (ulong)p;
                        }

                        if (childActivityID == null)
                            status = UnsafeNativeMethodsX.ManifestEtw.EventWrite(m_regHandle, ref eventDescriptor, argCount, userData);
                        else
                            status = UnsafeNativeMethodsX.ManifestEtw.EventWriteTransfer(m_regHandle, ref eventDescriptor, null, childActivityID, argCount, userData);

                        for (int i = 0; i < stringIndex; ++i)
                        {
                            rgGCHandle[i].Free();
                        }
                    }

                }
            }

            if (status != 0)
            {
                SetLastError((int)status);
                return false;
            }

            return true;
        }

        /// <summary>
        /// WriteEvent, method to be used by generated code on a derived class
        /// </summary>
        /// <param name="eventDescriptor">
        /// Event Descriptor for this event. 
        /// </param>
        /// <param name="childActivityID">
        /// If this event is generating a child activity (WriteEventTransfer related activity) this is child activity
        /// This can be null for events that do not generate a child activity.  
        /// </param>
        /// <param name="dataCount">
        /// number of event descriptors 
        /// </param>
        /// <param name="data">
        /// pointer  do the event data
        /// </param>
        // <SecurityKernel Critical="True" Ring="0">
        // <CallsSuppressUnmanagedCode Name="UnsafeNativeMethods.ManifestEtw.EventWrite(System.Int64,EventDescriptor&,System.UInt32,System.Void*):System.UInt32" />
        // </SecurityKernel>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1045:DoNotPassTypesByReference")]
        [System.Security.SecurityCritical]
        internal unsafe protected bool WriteEvent(ref EventDescriptor eventDescriptor, Guid* childActivityID, int dataCount, IntPtr data)
        {
            int status;
            if (childActivityID == null)
            {
                status = UnsafeNativeMethodsX.ManifestEtw.EventWrite(m_regHandle, ref eventDescriptor, dataCount, (EventData*)data);
            }
            else
            {
                // activity transfers are supported only for events that specify the Send or Receive opcode
                Contract.Assert((EventOpcode)eventDescriptor.Opcode == EventOpcode.Send || 
                                (EventOpcode)eventDescriptor.Opcode == EventOpcode.Receive);
                status = UnsafeNativeMethodsX.ManifestEtw.EventWriteTransfer(m_regHandle, ref eventDescriptor, null, childActivityID, dataCount, (EventData*)data);
            }
            if (status != 0)
            {
                SetLastError(status);
                return false;
            }
            return true;
        }

        [System.Security.SecurityCritical]
        internal unsafe protected bool WriteEventString(EventLevel level, long keywords, string msg)
        {
            int status;

            status = UnsafeNativeMethodsX.ManifestEtw.EventWriteString(m_regHandle, (byte) level, keywords, msg);

            if (status != 0)
            {
                SetLastError(status);
                return false;
            }
            return true;
        }

        // These are look-alikes to the Manifest based ETW OS APIs that have been shimmed to work
        // either with Manifest ETW or Classic ETW (if Manifest based ETW is not available).  
        [SecurityCritical]
        private unsafe uint EventRegister(ref Guid providerId, UnsafeNativeMethodsX.ManifestEtw.EtwEnableCallback enableCallback)
        {
            m_providerId = providerId;
            m_etwCallback = enableCallback;
            return UnsafeNativeMethodsX.ManifestEtw.EventRegister(ref providerId, enableCallback, null, ref m_regHandle);
        }

        [SecurityCritical]
        private uint EventUnregister()
        {
            uint status = UnsafeNativeMethodsX.ManifestEtw.EventUnregister(m_regHandle);
            m_regHandle = 0;
            return status;
        }

        static int[] nibblebits = {0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4};
        private static int bitcount(uint n)
        {
            int count = 0;
            for(; n != 0; n = n >> 4)
                count += nibblebits[n & 0x0f];
            return count;
        }
        private static int bitindex(uint n)
        {
            Contract.Assert(bitcount(n) == 1);
            int idx = 0;
            while ((n & (1 << idx)) == 0)
                idx++;
            return idx;
        }
    }
}
