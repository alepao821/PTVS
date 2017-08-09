// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Editor;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Parsing;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudioTools;

namespace Microsoft.PythonTools.Intellisense {
    using AP = AnalysisProtocol;

    sealed class BufferParser : IDisposable {
        private readonly Timer _timer;
        internal readonly PythonEditorServices _services;
        internal readonly AnalysisEntry AnalysisEntry;

        private readonly VsProjectAnalyzer _parser;
        private IList<ITextBuffer> _buffers;
        private bool _parsing, _requeue, _textChange;
        private ITextDocument _document;

        /// <summary>
        /// Maps between buffer ID and buffer info.
        /// </summary>
        private Dictionary<int, PythonTextBufferInfo> _bufferIdMapping = new Dictionary<int, PythonTextBufferInfo>();

        private const int ReparseDelay = 1000;      // delay in MS before we re-parse a buffer w/ non-line changes.

        public static readonly object DoNotParse = new object();

        internal static async Task<BufferParser> CreateAsync(
            AnalysisEntry analysis,
            VsProjectAnalyzer parser,
            ITextBuffer buffer
        ) {
            var res = new BufferParser(analysis, parser, buffer);

            using (new DebugTimer("BufferParser.ParseBuffers", 100)) {
                // lock not necessary for _bufferInfo, no one has access to us yet...
                await res.ParseBuffers(new[] { buffer.CurrentSnapshot }, new[] { res._services.GetBufferInfo(buffer) });
            }

            return res;
        }

        private BufferParser(AnalysisEntry analysis, VsProjectAnalyzer parser, ITextBuffer buffer) {
            Debug.Assert(analysis != null);

            _services = parser._services;
            _parser = parser;
            _timer = new Timer(ReparseTimer, null, Timeout.Infinite, Timeout.Infinite);
            _buffers = new[] { buffer };
            AnalysisEntry = analysis;

            InitBuffer(buffer, 0);
        }

        public PythonTextBufferInfo GetBuffer(ITextBuffer buffer) {
            return buffer == null ? null : _services.GetBufferInfo(buffer);
        }

        public PythonTextBufferInfo GetBuffer(int bufferId) {
            lock (this) {
                PythonTextBufferInfo res;
                _bufferIdMapping.TryGetValue(bufferId, out res);
                return res;
            }
        }

        /// <summary>
        /// Indicates that the specified buffer ID has been analyzed with this version.
        /// </summary>
        /// <returns>
        /// True if the specified version is newer than the last one we had received.
        /// </returns>
        public bool Analyzed(int bufferId, int version) {
            return GetBuffer(bufferId)?.UpdateLastReceivedAnalysis(version) ?? false;
        }

        /// <summary>
        /// Indicates that the specified buffer ID has been parsed with this version.
        /// </summary>
        /// <returns>
        /// True if the specified version is newer than the last one we had received.
        /// </returns>
        public bool Parsed(int bufferId, int version) {
            return GetBuffer(bufferId)?.UpdateLastReceivedParse(version) ?? false;
        }

        internal ITextSnapshot GetLastSentSnapshot(ITextBuffer buffer) {
            return GetBuffer(buffer)?.LastSentSnapshot;
        }

        internal void SetLastSentSnapshot(ITextSnapshot snapshot) {
            if (snapshot == null) {
                Debug.Fail("null snapshot");
                return;
            }

            GetBuffer(snapshot.TextBuffer).LastSentSnapshot = snapshot;
        }

        public ITextBuffer[] Buffers {
            get {
                return _buffers.Where(
                    x => !x.Properties.ContainsProperty(DoNotParse)
                ).ToArray();
            }
        }

        internal void AddBuffer(ITextBuffer textBuffer) {
            lock (this) {
                if (_buffers.Contains(textBuffer)) {
                    return;
                }

                EnsureMutableBuffers();
                _buffers.Add(textBuffer);
                InitBuffer(textBuffer, _buffers.Count - 1);

                _parser.ConnectErrorList(AnalysisEntry, textBuffer);
            }
        }

        internal int RemoveBuffer(ITextBuffer subjectBuffer) {
            lock (this) {
                EnsureMutableBuffers();
                UninitBuffer(subjectBuffer);
                _buffers.Remove(subjectBuffer);

                _parser.DisconnectErrorList(AnalysisEntry, subjectBuffer);

                return _buffers.Count;
            }
        }

        internal void UninitBuffer(ITextBuffer subjectBuffer) {
            PythonTextBufferInfo.TryDispose(subjectBuffer);

            if (_document != null) {
                _document.EncodingChanged -= EncodingChanged;
                _document = null;
            }
        }

        private void InitBuffer(ITextBuffer buffer, int id = 0) {
            var bi = _services.GetBufferInfo(buffer);
            if (!bi.SetAnalysisEntryId(id)) {
                Debug.Fail("Buffer is already initialized");
                return;
            }

            bi.OnChangedLowPriority += BufferChangedLowPriority;

            lock (this) {
                _bufferIdMapping[id] = bi;
            }

            ITextDocument doc;
            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out doc) && doc != _document) {
                if (_document != null) {
                    _document.EncodingChanged -= EncodingChanged;
                }
                _document = doc;
                if (_document != null) {
                    _document.EncodingChanged += EncodingChanged;
                }
            }
        }

        private void EnsureMutableBuffers() {
            if (_buffers.IsReadOnly) {
                _buffers = new List<ITextBuffer>(_buffers);
            }
        }

        internal void ReparseTimer(object unused) {
            RequeueWorker();
        }

        internal void ReparseWorker(object unused) {
            ITextSnapshot[] snapshots;
            PythonTextBufferInfo[] bufferInfos;
            lock (this) {
                if (_parsing) {
                    return;
                }

                _parsing = true;
                var buffers = Buffers;
                bufferInfos = buffers.Select(b => _services.GetBufferInfo(b)).ToArray();
                snapshots = bufferInfos.Select(b => b.CurrentSnapshot).ToArray();
            }

            ParseBuffers(snapshots, bufferInfos).WaitAndHandleAllExceptions(_services.Site);

            lock (this) {
                _parsing = false;
                if (_requeue) {
                    RequeueWorker();
                }
                _requeue = false;
            }
        }

        public async Task EnsureCodeSyncedAsync(ITextBuffer buffer) {
            var lastSent = GetLastSentSnapshot(buffer);
            if (lastSent != buffer.CurrentSnapshot) {
                await ParseBuffers(
                    new[] { buffer.CurrentSnapshot },
                    new[] { _services.GetBufferInfo(buffer) }
                );
            }
        }

        private async Task ParseBuffers(ITextSnapshot[] snapshots, PythonTextBufferInfo[] bufferInfos) {
            var indentationSeverity = _services.Python?.GeneralOptions.IndentationInconsistencySeverity ?? Severity.Ignore;
            AnalysisEntry entry = AnalysisEntry;

            List<AP.FileUpdate> updates = new List<AP.FileUpdate>();
            lock (this) {
                for (int i = 0; i < snapshots.Length; i++) {
                    var snapshot = snapshots[i];
                    var bufferInfo = bufferInfos[i];

                    if (snapshot.TextBuffer.Properties.ContainsProperty(DoNotParse) ||
                        snapshot.IsReplBufferWithCommand()) {
                        continue;
                    }

                    var lastSent = bufferInfo.LastSentSnapshot;
                    if (lastSent == null || lastSent.TextBuffer != snapshot.TextBuffer) {
                        // First time parsing from a live buffer, send the entire
                        // file and set our initial snapshot.  We'll roll forward
                        // to new snapshots when we receive the errors event.  This
                        // just makes sure that the content is in sync.
                        updates.Add(
                            new AP.FileUpdate() {
                                content = snapshot.GetText(),
                                version = snapshot.Version.VersionNumber,
                                bufferId = bufferInfo.AnalysisEntryId,
                                kind = AP.FileUpdateKind.reset
                            }
                        );
                    } else {
                        if (lastSent.Version == snapshot.Version) {
                            // this snapshot is up to date...
                            continue;
                        }

                        List<AP.VersionChanges> versions = new List<AnalysisProtocol.VersionChanges>();
                        for (var curVersion = lastSent.Version;
                            curVersion != snapshot.Version;
                            curVersion = curVersion.Next) {
                            versions.Add(
                                new AP.VersionChanges() {
                                    changes = GetChanges(curVersion)
                                }
                            );
                        }

                        updates.Add(
                            new AP.FileUpdate() {
                                versions = versions.ToArray(),
                                version = snapshot.Version.VersionNumber,
                                bufferId = bufferInfo.AnalysisEntryId,
                                kind = AP.FileUpdateKind.changes
                            }
                        );
                    }

                    Debug.WriteLine("Added parse request {0}", snapshot.Version.VersionNumber);
                    entry.AnalysisCookie = new SnapshotCookie(snapshot);  // TODO: What about multiple snapshots?
                    SetLastSentSnapshot(snapshot);
                }
            }

            if (updates.Count != 0) {
                _parser._analysisComplete = false;
                Interlocked.Increment(ref _parser._parsePending);

                var res = await _parser.SendRequestAsync(
                    new AP.FileUpdateRequest() {
                        fileId = entry.FileId,
                        updates = updates.ToArray()
                    }
                );

                if (res != null) {
                    Debug.Assert(res.failed != true);
                    _parser.OnAnalysisStarted();
#if DEBUG
                    for (int i = 0; i < bufferInfos.Length; i++) {
                        var snapshot = snapshots[i];
                        var buffer = bufferInfos[i];

                        string newCode;
                        if (res.newCode.TryGetValue(buffer.AnalysisEntryId, out newCode)) {
                            Debug.Assert(newCode == snapshot.GetText(), "Buffer content mismatch - safe to ignore");
                        }
                    }
#endif
                } else {
                    Interlocked.Decrement(ref _parser._parsePending);
                }
            }
        }

        private static AP.ChangeInfo[] GetChanges(ITextVersion curVersion) {
            Debug.WriteLine("Changes for version {0}", curVersion.VersionNumber);
            var changes = new List<AP.ChangeInfo>();
            if (curVersion.Changes != null) {
                foreach (var change in curVersion.Changes) {
                    Debug.WriteLine("Changes for version {0} {1} {2}", change.OldPosition, change.OldLength, change.NewText);
                    
                    changes.Add(
                        new AP.ChangeInfo() {
                            start = change.OldPosition,
                            length = change.OldLength,
                            newText = change.NewText
                        }
                    );
                }
            }
            return changes.ToArray();
        }

        internal void EncodingChanged(object sender, EncodingChangedEventArgs e) {
            lock (this) {
                if (_parsing) {
                    // we are currently parsing, just reque when we complete
                    _requeue = true;
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                } else {
                    Requeue();
                }
            }
        }

        internal void BufferChangedLowPriority(object sender, TextContentChangedEventArgs e) {
            lock (this) {
                // only immediately re-parse on line changes after we've seen a text change.

                if (_parsing) {
                    // we are currently parsing, just reque when we complete
                    _requeue = true;
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                } else if (LineAndTextChanges(e)) {
                    // user pressed enter, we should reque immediately
                    Requeue();
                } else {
                    // parse if the user doesn't do anything for a while.
                    _textChange = IncludesTextChanges(e);
                    _timer.Change(ReparseDelay, Timeout.Infinite);
                }
            }
        }

        internal void Requeue() {
            RequeueWorker();
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void RequeueWorker() {
            ThreadPool.QueueUserWorkItem(ReparseWorker);
        }

        /// <summary>
        /// Used to track if we have line + text changes, just text changes, or just line changes.
        /// 
        /// If we have text changes followed by a line change we want to immediately reparse.
        /// If we have just text changes we want to reparse in ReparseDelay ms from the last change.
        /// If we have just repeated line changes (e.g. someone's holding down enter) we don't want to
        ///     repeatedly reparse, instead we want to wait ReparseDelay ms.
        /// </summary>
        private bool LineAndTextChanges(TextContentChangedEventArgs e) {
            if (_textChange) {
                _textChange = false;
                return e.Changes.IncludesLineChanges;
            }

            bool mixedChanges = false;
            if (e.Changes.IncludesLineChanges) {
                mixedChanges = IncludesTextChanges(e);
            }

            return mixedChanges;
        }

        /// <summary>
        /// Returns true if the change incldues text changes (not just line changes).
        /// </summary>
        private static bool IncludesTextChanges(TextContentChangedEventArgs e) {
            bool mixedChanges = false;
            foreach (var change in e.Changes) {
                if (!string.IsNullOrEmpty(change.OldText) || change.NewText != Environment.NewLine) {
                    mixedChanges = true;
                    break;
                }
            }
            return mixedChanges;
        }

        public void Dispose() {
            foreach (var buffer in _buffers) {
                UninitBuffer(buffer);
            }
            _timer.Dispose();
            AnalysisEntry.ClearBufferParser(this);
        }

        internal ITextDocument Document {
            get {
                return _document;
            }
        }
    }
}
