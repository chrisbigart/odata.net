﻿//---------------------------------------------------------------------
// <copyright file="ODataMultipartMixedBatchWriter.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

namespace Microsoft.OData.MultipartMixed
{
    #region Namespaces
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
#if PORTABLELIB
    using System.Threading.Tasks;
#endif
    #endregion Namespaces

    internal sealed class ODataMultipartMixedBatchWriter : ODataBatchWriter
    {
        /// <summary>The boundary string for the batch structure itself.</summary>
        private readonly string batchBoundary;

        /// <summary>
        /// Gets the writer's output context as the real runtime type.
        /// </summary>
        private readonly ODataRawOutputContext rawOutputContext;

        /// <summary>
        /// The boundary string for the current changeset (only set when writing a changeset,
        /// e.g., after WriteStartChangeSet has been called and before WriteEndChangeSet is called).
        /// </summary>
        /// <remarks>When not writing a changeset this field is null.</remarks>
        private string changeSetBoundary;

        /// <summary>
        /// A flag to indicate whether the batch start boundary has been written or not; important to support writing of empty batches.
        /// </summary>
        private bool batchStartBoundaryWritten;

        /// <summary>
        /// A flags to indicate whether the current changeset start boundary has been written or not.
        /// This is false if a changeset has been started by no changeset boundary was written, and true once the first changeset
        /// boundary for the current changeset has been written.
        /// </summary>
        private bool changesetStartBoundaryWritten;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="rawOutputContext">The output context to write to.</param>
        /// <param name="batchBoundary">The boundary string for the batch structure itself.</param>
        internal ODataMultipartMixedBatchWriter(ODataRawOutputContext rawOutputContext, string batchBoundary)
            : base(rawOutputContext)
        {
            Debug.Assert(rawOutputContext != null, "rawOutputContext != null");
            ExceptionUtils.CheckArgumentNotNull(batchBoundary, "batchBoundary is null");
            this.batchBoundary = batchBoundary;
            this.rawOutputContext = rawOutputContext;
            this.rawOutputContext.InitializeRawValueWriter();
        }

        /// <summary>
        /// The message for the operation that is currently written; or null if no operation is written right now.
        /// </summary>
        private ODataBatchOperationMessage CurrentOperationMessage
        {
            get
            {
                Debug.Assert(this.CurrentOperationRequestMessage == null || this.CurrentOperationResponseMessage == null,
                    "Only request or response message can be set, not both.");
                if (this.CurrentOperationRequestMessage != null)
                {
                    Debug.Assert(!this.rawOutputContext.WritingResponse, "Request message can only be set when writing request.");
                    return this.CurrentOperationRequestMessage.OperationMessage;
                }
                else if (this.CurrentOperationResponseMessage != null)
                {
                    Debug.Assert(this.rawOutputContext.WritingResponse, "Response message can only be set when writing response.");
                    return this.CurrentOperationResponseMessage.OperationMessage;
                }
                else
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// This method is called to notify that the content stream for a batch operation has been requested.
        /// </summary>
        public override void BatchOperationContentStreamRequested()
        {
            // Write any pending data and flush the batch writer to the async buffered stream
            this.StartBatchOperationContent();

            // Flush the async buffered stream to the underlying message stream (if there's any)
            this.rawOutputContext.FlushBuffers();

            // Dispose the batch writer (since we are now writing the operation content) and set the corresponding state.
            this.DisposeBatchWriterAndSetContentStreamRequestedState();
        }

#if PORTABLELIB
        /// <summary>
        /// This method is called to notify that the content stream for a batch operation has been requested.
        /// </summary>
        /// <returns>
        /// A task representing any action that is running as part of the status change of the operation;
        /// null if no such action exists.
        /// </returns>
        public override Task BatchOperationContentStreamRequestedAsync()
        {
            // Write any pending data and flush the batch writer to the async buffered stream
            this.StartBatchOperationContent();

            // Asynchronously flush the async buffered stream to the underlying message stream (if there's any);
            // then dispose the batch writer (since we are now writing the operation content) and set the corresponding state.
            return this.rawOutputContext.FlushBuffersAsync()
                .FollowOnSuccessWith(task => this.DisposeBatchWriterAndSetContentStreamRequestedState());
        }
#endif

        /// <summary>
        /// This method is called to notify that the content stream of a batch operation has been disposed.
        /// </summary>
        public override void BatchOperationContentStreamDisposed()
        {
            Debug.Assert(this.CurrentOperationMessage != null, "Expected non-null operation message!");

            this.SetState(BatchWriterState.OperationStreamDisposed);
            this.CurrentOperationRequestMessage = null;
            this.CurrentOperationResponseMessage = null;
            this.rawOutputContext.InitializeRawValueWriter();
        }

        /// <summary>
        /// This method notifies the listener, that an in-stream error is to be written.
        /// </summary>
        /// <remarks>
        /// This listener can choose to fail, if the currently written payload doesn't support in-stream error at this position.
        /// If the listener returns, the writer should not allow any more writing, since the in-stream error is the last thing in the payload.
        /// </remarks>
        public override void OnInStreamError()
        {
            this.rawOutputContext.VerifyNotDisposed();
            this.SetState(BatchWriterState.Error);
            this.rawOutputContext.TextWriter.Flush();

            // The OData protocol spec did not defined the behavior when an exception is encountered outside of a batch operation. The batch writer
            // should not allow WriteError in this case. Note that WCF DS Server does serialize the error in XML format when it encounters one outside of a
            // batch operation.
            throw new ODataException(Strings.ODataBatchWriter_CannotWriteInStreamErrorForBatch);
        }

        /// <summary>
        /// Flush the output.
        /// </summary>
        protected override void FlushSynchronously()
        {
            this.rawOutputContext.Flush();
        }

#if PORTABLELIB
        /// <summary>
        /// Flush the output.
        /// </summary>
        /// <returns>Task representing the pending flush operation.</returns>
        protected override Task FlushAsynchronously()
        {
            return this.rawOutputContext.FlushAsync();
        }
#endif

        /// <summary>
        /// Starts a new changeset - implementation of the actual functionality.
        /// </summary>
        protected override void WriteStartChangesetImplementation()
        {
            // write pending message data (headers, response line) for a previously unclosed message/request
            this.WritePendingMessageData(true);

            // important to do this first since it will set up the change set boundary.
            this.SetState(BatchWriterState.ChangesetStarted);
            Debug.Assert(this.changeSetBoundary != null, "this.changeSetBoundary != null");

            // write the boundary string
            ODataMultipartMixedBatchWriterUtils.WriteStartBoundary(this.rawOutputContext.TextWriter, this.batchBoundary, !this.batchStartBoundaryWritten);
            this.batchStartBoundaryWritten = true;

            // write the change set headers
            ODataMultipartMixedBatchWriterUtils.WriteChangeSetPreamble(this.rawOutputContext.TextWriter, this.changeSetBoundary);
            this.changesetStartBoundaryWritten = false;
        }

        /// <summary>
        /// Creates an <see cref="ODataBatchOperationRequestMessage"/> for writing an operation of a batch request - implementation of the actual functionality.
        /// </summary>
        /// <param name="method">The Http method to be used for this request operation.</param>
        /// <param name="uri">The Uri to be used for this request operation.</param>
        /// <param name="contentId">The Content-ID value to write in ChangeSet head.</param>
        /// <param name="payloadUriOption">The format of operation Request-URI, which could be AbsoluteUri, AbsoluteResourcePathAndHost, or RelativeResourcePath.</param>
        /// <returns>The message that can be used to write the request operation.</returns>
        protected override ODataBatchOperationRequestMessage CreateOperationRequestMessageImplementation(string method, Uri uri, string contentId, BatchPayloadUriOption payloadUriOption)
        {
            // write pending message data (headers, response line) for a previously unclosed message/request
            this.WritePendingMessageData(true);

            // Add a potential Content-ID header to the URL resolver so that it will be available
            // to subsequent operations.
            // Note that what we add here is the Content-ID header of the previous operation (if any).
            // This also means that the Content-ID of the last operation in a changeset will never get
            // added to the cache which is fine since we cannot reference it anywhere.
            if (this.CurrentOperationContentId != null)
            {
                AddToPayloadUriConverter(this.CurrentOperationContentId);
            }

            this.InterceptException(() => uri = CreateOperationRequestUriWrapper(uri, this.rawOutputContext.MessageWriterSettings.BaseUri));

            // create the new request operation
            this.CurrentOperationRequestMessage = BuildOperationRequestMessage(
                this.rawOutputContext.OutputStream,
                method,
                uri);

            if (this.changeSetBoundary != null)
            {
                this.RememberContentIdHeader(contentId);
            }

            this.SetState(BatchWriterState.OperationCreated);

            // write the operation's start boundary string
            this.WriteStartBoundaryForOperation();

            // write the headers and request line
            ODataMultipartMixedBatchWriterUtils.WriteRequestPreamble(this.rawOutputContext.TextWriter, method, uri,
                this.rawOutputContext.MessageWriterSettings.BaseUri, changeSetBoundary != null, contentId,
                payloadUriOption);

            return this.CurrentOperationRequestMessage;
        }

        /// <summary>
        /// Ends a batch - implementation of the actual functionality.
        /// </summary>
        protected override void WriteEndBatchImplementation()
        {
            Debug.Assert(
                this.batchStartBoundaryWritten || this.CurrentOperationMessage == null,
                "If not batch boundary was written we must not have an active message.");

            // write pending message data (headers, response line) for a previously unclosed message/request
            this.WritePendingMessageData(true);

            this.SetState(BatchWriterState.BatchCompleted);

            // write the end boundary for the batch
            ODataMultipartMixedBatchWriterUtils.WriteEndBoundary(this.rawOutputContext.TextWriter, this.batchBoundary, !this.batchStartBoundaryWritten);

            // For compatibility with WCF DS we write a newline after the end batch boundary.
            // Technically it's not needed, but it doesn't violate anything either.
            this.rawOutputContext.TextWriter.WriteLine();
        }

        /// <summary>
        /// Ends an active changeset - implementation of the actual functionality.
        /// </summary>
        protected override void WriteEndChangesetImplementation()
        {
            // write pending message data (headers, response line) for a previously unclosed message/request
            this.WritePendingMessageData(true);

            string currentChangeSetBoundary = this.changeSetBoundary;

            // change the state first so we validate the change set boundary before attempting to write it.
            this.SetState(BatchWriterState.ChangesetCompleted);

            // In the case of an empty changeset the start changeset boundary has not been written yet
            // we will leave it like that, since we want the empty changeset to be represented only as
            // the end changeset boundary.
            // Due to WCF DS V2 compatibility we must not write the start boundary in this case
            // otherwise WCF DS V2 won't be able to read it (it fails on the start-end boundary empty changeset).

            // write the end boundary for the change set
            ODataMultipartMixedBatchWriterUtils.WriteEndBoundary(this.rawOutputContext.TextWriter, currentChangeSetBoundary, !this.changesetStartBoundaryWritten);
        }

        /// <summary>
        /// Creates an <see cref="ODataBatchOperationResponseMessage"/> for writing an operation of a batch response - implementation of the actual functionality.
        /// </summary>
        /// <param name="contentId">The Content-ID value to write in ChangeSet head.</param>
        /// <returns>The message that can be used to write the response operation.</returns>
        protected override ODataBatchOperationResponseMessage CreateOperationResponseMessageImplementation(string contentId)
        {
            this.WritePendingMessageData(true);

            // In responses we don't need to use our batch URL resolver, since there are no cross referencing URLs
            // so use the URL resolver from the batch message instead.
            this.CurrentOperationResponseMessage = BuildOperationResponseMessage(
                this.rawOutputContext.OutputStream);

            this.SetState(BatchWriterState.OperationCreated);

            Debug.Assert(this.CurrentOperationContentId == null, "The Content-ID header is only supported in request messages.");

            // write the operation's start boundary string
            this.WriteStartBoundaryForOperation();

            // write the headers and request separator line
            ODataMultipartMixedBatchWriterUtils.WriteResponsePreamble(this.rawOutputContext.TextWriter, changeSetBoundary != null, contentId);

            return this.CurrentOperationResponseMessage;
        }

        /// <summary>
        /// Additional processing required when setting a new writer state.
        /// </summary>
        /// <param name="newState">The writer state to transition into.</param>
        protected override void SetStateImplementation(BatchWriterState newState)
        {
            // Sets the changeset boundary for changeset boundary changes.
            switch (newState)
            {
                case BatchWriterState.BatchStarted:
                    Debug.Assert(!this.batchStartBoundaryWritten, "The batch boundary must not be written before calling WriteStartBatch.");
                    break;
                case BatchWriterState.ChangesetStarted:
                    Debug.Assert(this.changeSetBoundary == null, "this.changeSetBoundary == null");
                    this.changeSetBoundary = ODataMultipartMixedBatchWriterUtils.CreateChangeSetBoundary(this.rawOutputContext.WritingResponse);
                    break;
                case BatchWriterState.ChangesetCompleted:
                    Debug.Assert(this.changeSetBoundary != null, "this.changeSetBoundary != null");
                    this.changeSetBoundary = null;
                    break;
            }
        }

        protected override void ValidateTransitionImplementation(BatchWriterState newState)
        {
            // Additional validation for multipart/mixed batch writer.
            ValidateTransitionAgainstChangesetBoundary(newState, this.changeSetBoundary);
        }

        /// <summary>
        /// Verifies that the writer is not disposed.
        /// </summary>
        protected override void VerifyNotDisposed()
        {
            this.rawOutputContext.VerifyNotDisposed();
        }

        /// <summary>
        /// Writer specific implementation to verify that CreateOperationRequestMessage is valid.
        /// For Multipart/Mixed writer, this implementation verifies that, for the case within a changeset,
        /// CreateOperationRequestMessage is valid.
        /// </summary>
        /// <param name="method">The HTTP method to be validated.</param>
        /// <param name="uri">The Uri to be used for this request operation.</param>
        /// <param name="contentId">The content Id string to be validated.</param>
        protected override void VerifyCanCreateOperationRequestMessageImplementation(string method, Uri uri, string contentId)
        {
            if (this.changeSetBoundary != null)
            {
                if (HttpUtils.IsQueryMethod(method))
                {
                    this.ThrowODataException(Strings.ODataBatch_InvalidHttpMethodForChangeSetRequest(method));
                }

                if (string.IsNullOrEmpty(contentId))
                {
                    this.ThrowODataException(Strings.ODataBatchOperationHeaderDictionary_KeyNotFound(ODataConstants.ContentIdHeader));
                }
            }
        }

        /// <summary>
        /// Starts a new batch - implementation of the actual functionality.
        /// </summary>
        protected override void WriteStartBatchImplementation()
        {
            this.SetState(BatchWriterState.BatchStarted);
        }

        /// <summary>
        /// Writes all the pending headers and prepares the writer to write a content of the operation.
        /// </summary>
        private void StartBatchOperationContent()
        {
            Debug.Assert(this.CurrentOperationMessage != null, "Expected non-null operation message!");
            Debug.Assert(this.rawOutputContext.TextWriter != null, "Must have a batch writer!");

            // write the pending headers (if any)
            this.WritePendingMessageData(false);

            // flush the text writer to make sure all buffers of the text writer
            // are flushed to the underlying async stream
            this.rawOutputContext.TextWriter.Flush();
        }

        /// <summary>
        /// Disposes the batch writer and set the 'OperationStreamRequested' batch writer state;
        /// called after the flush operation(s) have completed.
        /// </summary>
        private void DisposeBatchWriterAndSetContentStreamRequestedState()
        {
            this.rawOutputContext.CloseWriter();

            this.SetState(BatchWriterState.OperationStreamRequested);
        }

        /// <summary>
        /// Writes the start boundary for an operation. This is either the batch or the changeset boundary.
        /// </summary>
        private void WriteStartBoundaryForOperation()
        {
            if (this.changeSetBoundary == null)
            {
                ODataMultipartMixedBatchWriterUtils.WriteStartBoundary(this.rawOutputContext.TextWriter, this.batchBoundary, !this.batchStartBoundaryWritten);
                this.batchStartBoundaryWritten = true;
            }
            else
            {
                ODataMultipartMixedBatchWriterUtils.WriteStartBoundary(this.rawOutputContext.TextWriter, this.changeSetBoundary, !this.changesetStartBoundaryWritten);
                this.changesetStartBoundaryWritten = true;
            }
        }

        /// <summary>
        /// Write any pending headers for the current operation message (if any).
        /// </summary>
        /// <param name="reportMessageCompleted">
        /// A flag to control whether after writing the pending data we report writing the message to be completed or not.
        /// </param>
        private void WritePendingMessageData(bool reportMessageCompleted)
        {
            if (this.CurrentOperationMessage != null)
            {
                Debug.Assert(this.rawOutputContext.TextWriter != null, "Must have a batch writer if pending data exists.");

                if (this.CurrentOperationResponseMessage != null)
                {
                    Debug.Assert(this.rawOutputContext.WritingResponse, "If the response message is available we must be writing response.");
                    int statusCode = this.CurrentOperationResponseMessage.StatusCode;
                    string statusMessage = HttpUtils.GetStatusMessage(statusCode);
                    this.rawOutputContext.TextWriter.WriteLine("{0} {1} {2}", ODataConstants.HttpVersionInBatching, statusCode, statusMessage);
                }

                IEnumerable<KeyValuePair<string, string>> headers = this.CurrentOperationMessage.Headers;
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> headerPair in headers)
                    {
                        string headerName = headerPair.Key;
                        string headerValue = headerPair.Value;
                        this.rawOutputContext.TextWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1}", headerName, headerValue));
                    }
                }

                // write CRLF after the headers (or request/response line if there are no headers)
                this.rawOutputContext.TextWriter.WriteLine();

                if (reportMessageCompleted)
                {
                    this.CurrentOperationMessage.PartHeaderProcessingCompleted();
                    this.CurrentOperationRequestMessage = null;
                    this.CurrentOperationResponseMessage = null;
                }
            }
        }

        /// <summary>
        /// Validates state transition is allowed if we are within a changeset.
        /// </summary>
        /// <param name="newState">Teh new writer state to transition into.</param>
        /// <param name="changeSetBoundary">The changeset boundary string.</param>
        private static void ValidateTransitionAgainstChangesetBoundary(BatchWriterState newState, string changeSetBoundary)
        {
            // make sure that we are not starting a changeset when one is already active
            if (newState == BatchWriterState.ChangesetStarted)
            {
                if (changeSetBoundary != null)
                {
                    throw new ODataException(Strings.ODataBatchWriter_CannotStartChangeSetWithActiveChangeSet);
                }
            }

            // make sure that we are not completing a changeset without an active changeset
            if (newState == BatchWriterState.ChangesetCompleted)
            {
                if (changeSetBoundary == null)
                {
                    throw new ODataException(Strings.ODataBatchWriter_CannotCompleteChangeSetWithoutActiveChangeSet);
                }
            }

            // make sure that we are not completing a batch while a changeset is still active
            if (newState == BatchWriterState.BatchCompleted)
            {
                if (changeSetBoundary != null)
                {
                    throw new ODataException(Strings.ODataBatchWriter_CannotCompleteBatchWithActiveChangeSet);
                }
            }
        }
    }
}
