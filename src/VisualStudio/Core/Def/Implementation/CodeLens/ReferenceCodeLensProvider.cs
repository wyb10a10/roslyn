﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeLens;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Wpf;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.ServiceHub.Client;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.LanguageServices.Remote;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;
using StreamJsonRpc;

namespace Microsoft.VisualStudio.LanguageServices.CodeLens
{
    [Export(typeof(IAsyncCodeLensDataPointProvider))]
    [Name(Id)]
    [ContentType(ContentTypeNames.CSharpContentType)]
    [ContentType(ContentTypeNames.VisualBasicContentType)]
    [LocalizedName(typeof(ServicesVSResources), "References")]
    [Priority(200)]
    [OptionUserModifiable(userModifiable: false)]
    [DetailsTemplateName("references")]
    internal class ReferenceCodeLensProvider : IAsyncCodeLensDataPointProvider
    {
        // these string are never exposed to users but internally used to identify 
        // each provider/servicehub connections and etc
        private const string Id = "C#/VB Reference Indicator Data Provider";
        private const string HubClientId = "ManagedLanguage.IDE.CodeLensOOP";
        private const string RoslynCodeAnalysis = "roslynCodeAnalysis";

        private readonly HubClient _client;

        // use field rather than import constructor to break circular MEF dependency issue
        [Import]
        private ICodeLensCallbackService _codeLensCallbackService = null;

        public ReferenceCodeLensProvider()
        {
            _client = new HubClient(HubClientId);
        }

        public Task<bool> CanCreateDataPointAsync(CodeLensDescriptor descriptor, CancellationToken token)
        {
            if (!descriptor.ApplicableToSpan.HasValue)
            {
                return SpecializedTasks.False;
            }

            // we allow all reference points. 
            // engine will call this for all points our roslyn code lens (reference) tagger tagged.
            return SpecializedTasks.True;
        }

        public async Task<IAsyncCodeLensDataPoint> CreateDataPointAsync(CodeLensDescriptor descriptor, CancellationToken cancellationToken)
        {
            // this let us to call back to VS and get some info from there
            var callbackRpc = _codeLensCallbackService.GetCallbackJsonRpc(this);

            var dataPoint = new DataPoint(descriptor, await GetConnectionAsync(cancellationToken).ConfigureAwait(false), callbackRpc);
            await dataPoint.TrackChangesAsync(cancellationToken).ConfigureAwait(false);

            return dataPoint;
        }

        private async Task<Stream> GetConnectionAsync(CancellationToken cancellationToken)
        {
            // any exception from this will be caught by codelens engine and saved to log file and ignored.
            // this follows existing code lens behavior and user experience on failure is owned by codelens engine
            var callbackRpc = _codeLensCallbackService.GetCallbackJsonRpc(this);
            var hostGroupId = await callbackRpc.InvokeWithCancellationAsync<string>(nameof(ICodeLensContext.GetHostGroupIdAsync), arguments: null, cancellationToken).ConfigureAwait(false);

            var hostGroup = new HostGroup(hostGroupId);
            var serviceDescriptor = new ServiceDescriptor(RoslynCodeAnalysis) { HostGroup = hostGroup };

            return await _client.RequestServiceAsync(serviceDescriptor, cancellationToken).ConfigureAwait(false);
        }

        private class DataPoint : IAsyncCodeLensDataPoint, IRemoteCodeLensDataPoint, IDisposable
        {
            private readonly JsonRpc _roslynRpc;
            private readonly JsonRpc _vsCallbackRpc;

            public DataPoint(CodeLensDescriptor descriptor, Stream stream, JsonRpc vsCallbackRpc)
            {
                this.Descriptor = descriptor;

                _roslynRpc = new JsonRpc(new JsonRpcMessageHandler(stream, stream), target: this);
                _roslynRpc.JsonSerializer.Converters.Add(AggregateJsonConverter.Instance);

                _roslynRpc.StartListening();

                _vsCallbackRpc = vsCallbackRpc;
            }

            public event AsyncEventHandler InvalidatedAsync;

            public CodeLensDescriptor Descriptor { get; }

            public async Task<CodeLensDataPointDescriptor> GetDataAsync(CancellationToken cancellationToken)
            {
                // we always get data through VS rather than Roslyn OOP directly since we want final data rather than
                // raw data from Roslyn OOP such as razor find all reference results
                var referenceCount = await _vsCallbackRpc.InvokeWithCancellationAsync<ReferenceCount>(
                    nameof(ICodeLensContext.GetReferenceCountAsync), new object[] { this.Descriptor }, cancellationToken).ConfigureAwait(false);

                if (referenceCount == null)
                {
                    return null;
                }

                var referenceCountString = $"{referenceCount.Count}{(referenceCount.IsCapped ? "+" : string.Empty)}";

                return new CodeLensDataPointDescriptor()
                {
                    Description = referenceCount.Count == 1
                        ? string.Format(ServicesVSResources._0_reference, referenceCountString)
                        : string.Format(ServicesVSResources._0_references, referenceCountString),
                    IntValue = referenceCount.Count,
                    TooltipText = string.Format(ServicesVSResources.This_0_has_1_references, GetCodeElementKindsString(Descriptor.Kind), referenceCountString),
                    ImageId = null
                };

                string GetCodeElementKindsString(CodeElementKinds kind)
                {
                    switch (kind)
                    {
                        case CodeElementKinds.Method:
                            return ServicesVSResources.method;
                        case CodeElementKinds.Type:
                            return ServicesVSResources.type1;
                        case CodeElementKinds.Property:
                            return ServicesVSResources.property;
                        default:
                            // code lens engine will catch and ignore exception
                            // basically not showing data point
                            throw new NotSupportedException(nameof(kind));
                    }
                }
            }

            public async Task<CodeLensDetailsDescriptor> GetDetailsAsync(CancellationToken cancellationToken)
            {
                // we always get data through VS rather than Roslyn OOP directly since we want final data rather than
                // raw data from Roslyn OOP such as razor find all reference results
                var referenceLocationDescriptors = await _vsCallbackRpc.InvokeWithCancellationAsync<IEnumerable<ReferenceLocationDescriptor>>(
                    nameof(ICodeLensContext.FindReferenceLocationsAsync), new object[] { this.Descriptor }, cancellationToken).ConfigureAwait(false);

                var details = new CodeLensDetailsDescriptor
                {
                    Headers = new List<CodeLensDetailHeaderDescriptor>()
                    {
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.FilePath },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.LineNumber },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.ColumnNumber },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.ReferenceText },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.ReferenceStart },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.ReferenceEnd },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.ReferenceLongDescription },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.ReferenceImageId },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.TextBeforeReference2 },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.TextBeforeReference1 },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.TextAfterReference1 },
                        new CodeLensDetailHeaderDescriptor() { UniqueName = ReferenceEntryFieldNames.TextAfterReference2 },
                    },
                    Entries = referenceLocationDescriptors.Select(referenceLocationDescriptor =>
                    {
                        ImageId imageId = default;
                        if (referenceLocationDescriptor.Glyph.HasValue)
                        {
                            var moniker = referenceLocationDescriptor.Glyph.Value.GetImageMoniker();
                            imageId = new ImageId(moniker.Guid, moniker.Id);
                        }

                        return new CodeLensDetailEntryDescriptor()
                        {
                            // use default since reference codelens don't require special behaviors
                            NavigationCommand = null,
                            NavigationCommandArgs = null,
                            Tooltip = null,
                            Fields = new List<CodeLensDetailEntryField>()
                            {
                                new CodeLensDetailEntryField() { Text = Descriptor.FilePath },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.LineNumber.ToString() },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.ColumnNumber.ToString() },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.ReferenceLineText },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.ReferenceStart.ToString() },
                                new CodeLensDetailEntryField() { Text = (referenceLocationDescriptor.ReferenceStart + referenceLocationDescriptor.ReferenceLength).ToString() },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.LongDescription },
                                new CodeLensDetailEntryField() { ImageId = imageId },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.BeforeReferenceText2 },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.BeforeReferenceText1 },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.AfterReferenceText1 },
                                new CodeLensDetailEntryField() { Text = referenceLocationDescriptor.AfterReferenceText2 }
                            },
                        };
                    }).ToList(),

                    // use default behavior
                    PaneNavigationCommands = null
                };

                return details;
            }

            public void Invalidate()
            {
                // fire and forget
                // this get called from roslyn remote host
                InvalidatedAsync?.InvokeAsync(this, EventArgs.Empty);
            }

            public async Task TrackChangesAsync(CancellationToken cancellationToken)
            {
                var documentId = await GetDocumentIdAsync().ConfigureAwait(false);
                if (documentId == null)
                {
                    return;
                }

                // this asks Roslyn OOP to start track workspace changes and call back Invalidate on this type when there is one.
                // each data point owns 1 connection which is alive while data point is alive. and all communication is done through
                // that connection
                await _roslynRpc.InvokeWithCancellationAsync(
                    nameof(IRemoteCodeLensReferencesService.TrackCodeLensAsync), new object[] { documentId }, cancellationToken).ConfigureAwait(false);

                async Task<DocumentId> GetDocumentIdAsync()
                {
                    var guids = await _vsCallbackRpc.InvokeWithCancellationAsync<List<Guid>>(
                        nameof(ICodeLensContext.GetDocumentId), new object[] { this.Descriptor.ProjectGuid, this.Descriptor.FilePath }, cancellationToken).ConfigureAwait(false);
                    if (guids == null)
                    {
                        return null;
                    }

                    return DocumentId.CreateFromSerialized(
                        ProjectId.CreateFromSerialized(guids[0], this.Descriptor.ProjectGuid.ToString()), guids[1], this.Descriptor.FilePath);
                }
            }

            public void Dispose()
            {
                // done. let connection go
                _roslynRpc.Dispose();

                // vsCallbackRpc is shared and we don't own. it is owned by the code lens engine
                // don't dispose it
            }
        }
    }
}
