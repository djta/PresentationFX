//---------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All rights reserved.
// 
// File: IFlowDocumentFormatter.cs
//
// Description: Content formatter associated with FlowDocument.
//
// History:  
//  11/07/2005 : [....] - created.
//
//---------------------------------------------------------------------------

using System.Windows.Documents;     // ITextPointer

namespace MS.Internal.Documents 
{
    /// <summary>
    /// Bottomless content formatter associated with FlowDocument.
    /// </summary>
    internal interface IFlowDocumentFormatter
    {
        /// <summary>
        /// Responds to change affecting entire content of associated FlowDocument.
        /// </summary>
        /// <param name="affectsLayout">Whether change affects layout.</param>
        void OnContentInvalidated(bool affectsLayout);

        /// <summary>
        /// Responds to change affecting entire content of associated FlowDocument.
        /// </summary>
        /// <param name="affectsLayout">Whether change affects layout.</param>
        /// <param name="start">Start of the affected content range.</param>
        /// <param name="end">End of the affected content range.</param>
        void OnContentInvalidated(bool affectsLayout, ITextPointer start, ITextPointer end);

        /// <summary>
        /// Suspend formatting.
        /// </summary>
        void Suspend();

        /// <summary>
        /// Is layout data in a valid state.
        /// </summary>
        bool IsLayoutDataValid { get; }
    }
}
