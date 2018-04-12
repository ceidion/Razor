﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.Editor;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.VisualStudio.Editor.Razor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Microsoft.VisualStudio.LanguageServices.Razor.Editor
{
    internal class DefaultVisualStudioDocumentTracker : VisualStudioDocumentTracker
    {
        private readonly string _filePath;
        private readonly ProjectSnapshotManager _projectManager;
        private readonly EditorSettingsManager _editorSettingsManager;
        private readonly TextBufferProjectService _projectService;
        private readonly ITextBuffer _textBuffer;
        private readonly List<ITextView> _textViews;
        private readonly Workspace _workspace;
        private bool _isSupportedProject;
        private ProjectSnapshot _projectSnapshot;
        private string _projectPath;

        public override event EventHandler<ContextChangeEventArgs> ContextChanged;

        public DefaultVisualStudioDocumentTracker(
            string filePath,
            ProjectSnapshotManager projectManager,
            TextBufferProjectService projectService,
            EditorSettingsManager editorSettingsManager,
            Workspace workspace,
            ITextBuffer textBuffer)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException(Microsoft.VisualStudio.Editor.Razor.Resources.ArgumentCannotBeNullOrEmpty, nameof(filePath));
            }

            if (projectManager == null)
            {
                throw new ArgumentNullException(nameof(projectManager));
            }

            if (projectService == null)
            {
                throw new ArgumentNullException(nameof(projectService));
            }

            if (editorSettingsManager == null)
            {
                throw new ArgumentNullException(nameof(editorSettingsManager));
            }

            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            if (textBuffer == null)
            {
                throw new ArgumentNullException(nameof(textBuffer));
            }

            _filePath = filePath;
            _projectManager = projectManager;
            _projectService = projectService;
            _editorSettingsManager = editorSettingsManager;
            _textBuffer = textBuffer;
            _workspace = workspace; // For now we assume that the workspace is the always default VS workspace.

            _textViews = new List<ITextView>();
        }

        public override RazorConfiguration Configuration => _projectSnapshot.Configuration;

        public override EditorSettings EditorSettings => _editorSettingsManager.Current;

        public override IReadOnlyList<TagHelperDescriptor> TagHelpers => Array.Empty<TagHelperDescriptor>();

        public override bool IsSupportedProject => _isSupportedProject;

        public override Project Project =>
            _projectSnapshot.WorkspaceProject == null ?
            null :
            _workspace.CurrentSolution.GetProject(_projectSnapshot.WorkspaceProject.Id);

        internal override ProjectSnapshot ProjectSnapshot => _projectSnapshot;

        public override ITextBuffer TextBuffer => _textBuffer;

        public override IReadOnlyList<ITextView> TextViews => _textViews;

        public override Workspace Workspace => _workspace;

        public override string FilePath => _filePath;

        public override string ProjectPath => _projectPath;

        internal void AddTextView(ITextView textView)
        {
            if (textView == null)
            {
                throw new ArgumentNullException(nameof(textView));
            }

            if (!_textViews.Contains(textView))
            {
                _textViews.Add(textView);

                if (_textViews.Count == 1)
                {
                    Subscribe();
                }
            }
        }

        internal void RemoveTextView(ITextView textView)
        {
            if (textView == null)
            {
                throw new ArgumentNullException(nameof(textView));
            }

            if (_textViews.Contains(textView))
            {
                _textViews.Remove(textView);

                if (_textViews.Count == 0)
                {
                    Unsubscribe();
                }
            }
        }

        public override ITextView GetFocusedTextView()
        {
            for (var i = 0; i < TextViews.Count; i++)
            {
                if (TextViews[i].HasAggregateFocus)
                {
                    return TextViews[i];
                }
            }

            return null;
        }

        private void Subscribe()
        {
            // Fundamentally we have a Razor half of the world as as soon as the document is open - and then later 
            // the C# half of the world will be initialized. This code is in general pretty tolerant of 
            // unexpected /impossible states.
            //
            // We also want to successfully shut down if the buffer is something other than .cshtml.
            object hierarchy = null;
            string projectPath = null;
            var isSupportedProject = false;

            if (_textBuffer.ContentType.IsOfType(RazorLanguage.ContentType) &&

                // We expect the document to have a hierarchy even if it's not a real 'project'.
                // However the hierarchy can be null when the document is in the process of closing.
                (hierarchy = _projectService.GetHostProject(_textBuffer)) != null)
            {
                projectPath = _projectService.GetProjectPath(hierarchy);
                isSupportedProject = _projectService.IsSupportedProject(hierarchy);
            }

            if (!isSupportedProject || projectPath == null)
            {
                return;
            }

            _isSupportedProject = isSupportedProject;
            _projectPath = projectPath;
            _projectSnapshot = _projectManager.GetOrCreateProject(projectPath);
            _projectManager.Changed += ProjectManager_Changed;
            _editorSettingsManager.Changed += EditorSettingsManager_Changed;

            OnContextChanged(ContextChangeKind.ProjectChanged);
        }

        private void Unsubscribe()
        {
            _projectManager.Changed -= ProjectManager_Changed;
            _editorSettingsManager.Changed -= EditorSettingsManager_Changed;

            // Detached from project.
            _isSupportedProject = false;
            _projectSnapshot = null;
            OnContextChanged(kind: ContextChangeKind.ProjectChanged);
        }

        private void OnContextChanged(ContextChangeKind kind)
        {
            var handler = ContextChanged;
            if (handler != null)
            {
                handler(this, new ContextChangeEventArgs(kind));
            }
        }

        private void ProjectManager_Changed(object sender, ProjectChangeEventArgs e)
        {
            if (_projectPath != null &&
                string.Equals(_projectPath, e.Project.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                // This will be the new snapshot unless the project was removed.
                _projectSnapshot = e.Project;

                switch (e.Kind)
                {
                    case ProjectChangeKind.DocumentsChanged:

                        // Nothing to do.
                        break;

                    case ProjectChangeKind.ProjectAdded:
                    case ProjectChangeKind.ProjectChanged:

                        // Just an update
                        OnContextChanged(ContextChangeKind.ProjectChanged);
                        break;

                    case ProjectChangeKind.ProjectRemoved:

                        // Fall back to ephemeral project
                        _projectSnapshot = _projectManager.GetOrCreateProject(ProjectPath);
                        OnContextChanged(ContextChangeKind.ProjectChanged);
                        break;

                    case ProjectChangeKind.TagHelpersChanged:

                        // Just an update.
                        OnContextChanged(ContextChangeKind.TagHelpersChanged);
                        break;

                    default:
                        throw new InvalidOperationException($"Unknown ProjectChangeKind {e.Kind}");
                }
            }

            Debug.Assert(_projectSnapshot != null);
        }

        // Internal for testing
        internal void EditorSettingsManager_Changed(object sender, EditorSettingsChangedEventArgs args)
        {
            OnContextChanged(ContextChangeKind.EditorSettingsChanged);
        }
    }
}