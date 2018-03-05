﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using VSCommanding = Microsoft.VisualStudio.Commanding;

namespace Microsoft.CodeAnalysis.Editor.Implementation.Formatting
{
    internal partial class FormatCommandHandler
    {
        public VSCommanding.CommandState GetCommandState(FormatSelectionCommandArgs args)
        {
            return GetCommandState(args.SubjectBuffer);
        }

        public bool ExecuteCommand(FormatSelectionCommandArgs args, CommandExecutionContext context)
        {
            return TryExecuteCommand(args, context);
        }

        private bool TryExecuteCommand(FormatSelectionCommandArgs args, CommandExecutionContext context)
        {
            if (!args.SubjectBuffer.CanApplyChangeDocumentToWorkspace())
            {
                return false;
            }

            var document = args.SubjectBuffer.CurrentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
            if (document == null)
            {
                return false;
            }

            var formattingService = document.GetLanguageService<IEditorFormattingService>();
            if (formattingService == null || !formattingService.SupportsFormatSelection)
            {
                return false;
            }

            using (context.WaitContext.AddScope(allowCancellation: true, EditorFeaturesResources.Formatting_currently_selected_text))
            {
                var buffer = args.SubjectBuffer;

                // we only support single selection for now
                var selection = args.TextView.Selection.GetSnapshotSpansOnBuffer(buffer);
                if (selection.Count != 1)
                {
                    return false;
                }

                var formattingSpan = selection[0].Span.ToTextSpan();

                Format(args.TextView, document, formattingSpan, context.WaitContext.UserCancellationToken);

                // make behavior same as dev12. 
                // make sure we set selection back and set caret position at the end of selection
                // we can delete this code once razor side fixes a bug where it depends on this behavior (dev12) on formatting.
                var currentSelection = selection[0].TranslateTo(args.SubjectBuffer.CurrentSnapshot, SpanTrackingMode.EdgeExclusive);
                args.TextView.SetSelection(currentSelection);
                args.TextView.TryMoveCaretToAndEnsureVisible(currentSelection.End, ensureSpanVisibleOptions: EnsureSpanVisibleOptions.MinimumScroll);

                // We have handled this command
                return true;
            }
        }
    }
}
