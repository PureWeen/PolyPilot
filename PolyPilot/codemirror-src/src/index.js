// CodeMirror 6 bundle for PolyPilot
// Provides: EditorView, MergeView, language support, dark theme

import { EditorView, lineNumbers, highlightActiveLineGutter, highlightSpecialChars,
         drawSelection, highlightActiveLine, keymap, placeholder } from "@codemirror/view";
import { EditorState, Compartment } from "@codemirror/state";
import { defaultHighlightStyle, syntaxHighlighting, indentOnInput,
         bracketMatching, foldGutter, foldKeymap, LanguageDescription } from "@codemirror/language";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { searchKeymap, highlightSelectionMatches, openSearchPanel } from "@codemirror/search";
import { closeBrackets, closeBracketsKeymap } from "@codemirror/autocomplete";
import { MergeView } from "@codemirror/merge";
import { oneDark } from "@codemirror/theme-one-dark";
import { javascript } from "@codemirror/lang-javascript";
import { css } from "@codemirror/lang-css";
import { html } from "@codemirror/lang-html";
import { json } from "@codemirror/lang-json";
import { markdown } from "@codemirror/lang-markdown";
import { python } from "@codemirror/lang-python";
import { xml } from "@codemirror/lang-xml";
import { StreamLanguage } from "@codemirror/language";
import { csharp as csharpMode } from "@codemirror/legacy-modes/mode/clike";
import { shell as shellMode } from "@codemirror/legacy-modes/mode/shell";

// Language support via legacy modes
const csharpLanguage = StreamLanguage.define(csharpMode);
const shellLanguage = StreamLanguage.define(shellMode);

function csharp() { return csharpLanguage; }
function shell() { return shellLanguage; }

// Map file extensions to language support
function getLanguageForFile(filename) {
  if (!filename) return [];
  const ext = filename.split('.').pop()?.toLowerCase();
  switch (ext) {
    case 'cs': return [csharp()];
    case 'js': case 'mjs': case 'cjs': return [javascript()];
    case 'ts': case 'tsx': return [javascript({ typescript: true })];
    case 'jsx': return [javascript({ jsx: true })];
    case 'css': case 'scss': case 'less': return [css()];
    case 'html': case 'htm': case 'razor': case 'cshtml': return [html()];
    case 'json': return [json()];
    case 'md': case 'markdown': return [markdown()];
    case 'py': return [python()];
    case 'xml': case 'xaml': case 'csproj': case 'props': case 'targets': case 'sln': case 'slnx': return [xml()];
    case 'sh': case 'bash': case 'zsh': return [shell()];
    default: return [];
  }
}

// PolyPilot custom dark theme overlay
const polyPilotTheme = EditorView.theme({
  "&": {
    backgroundColor: "var(--bg-secondary, #1a1e2e)",
    color: "var(--text-primary, #c8d8f0)",
    fontSize: "var(--type-body, 0.72rem)",
    fontFamily: "var(--font-mono, 'SF Mono', Menlo, monospace)",
    borderRadius: "0 0 8px 8px",
  },
  ".cm-content": {
    caretColor: "var(--accent-primary, #4ea8d1)",
    padding: "4px 0",
  },
  ".cm-cursor": {
    borderLeftColor: "var(--accent-primary, #4ea8d1)",
  },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground": {
    backgroundColor: "rgba(78, 168, 209, 0.25) !important",
  },
  ".cm-gutters": {
    backgroundColor: "var(--bg-tertiary, rgba(255,255,255,0.03))",
    color: "var(--text-dim, rgba(200,216,240,0.3))",
    border: "none",
    borderRight: "1px solid var(--control-border, rgba(255,255,255,0.06))",
  },
  ".cm-gutterElement": {
    cursor: "pointer",
  },
  ".cm-activeLineGutter": {
    backgroundColor: "rgba(78, 168, 209, 0.1)",
  },
  ".cm-activeLine": {
    backgroundColor: "rgba(78, 168, 209, 0.06)",
  },
  ".cm-foldPlaceholder": {
    backgroundColor: "rgba(78, 168, 209, 0.15)",
    color: "var(--accent-primary, #4ea8d1)",
    border: "none",
    padding: "0 4px",
    borderRadius: "3px",
  },
  // Merge view specific
  ".cm-mergeView": {
    borderRadius: "0 0 8px 8px",
    overflow: "hidden",
  },
  ".cm-merge-a .cm-changedLine": {
    backgroundColor: "var(--diff-del-bg, rgba(248,81,73,0.1))",
  },
  ".cm-merge-b .cm-changedLine": {
    backgroundColor: "var(--diff-add-bg, rgba(63,185,80,0.1))",
  },
  ".cm-merge-a .cm-changedText": {
    backgroundColor: "var(--diff-del-bg, rgba(248,81,73,0.2))",
  },
  ".cm-merge-b .cm-changedText": {
    backgroundColor: "var(--diff-add-bg, rgba(63,185,80,0.2))",
  },
  ".cm-deletedChunk": {
    backgroundColor: "var(--diff-del-bg, rgba(248,81,73,0.08))",
  },
  // Collapsed unchanged sections
  ".cm-collapsedLines": {
    backgroundColor: "var(--bg-tertiary, rgba(255,255,255,0.03))",
    color: "var(--text-dim, rgba(200,216,240,0.4))",
    padding: "2px 12px",
    fontSize: "0.65rem",
    cursor: "pointer",
    borderTop: "1px solid var(--control-border, rgba(255,255,255,0.06))",
    borderBottom: "1px solid var(--control-border, rgba(255,255,255,0.06))",
  },
  ".cm-search": {
    backgroundColor: "var(--bg-secondary, #1a1e2e)",
  },
  ".cm-searchMatch": {
    backgroundColor: "rgba(255, 200, 50, 0.3)",
  },
  ".cm-searchMatch-selected": {
    backgroundColor: "rgba(255, 200, 50, 0.5)",
  },
  ".cm-panels": {
    backgroundColor: "var(--bg-tertiary, rgba(255,255,255,0.05))",
    color: "var(--text-primary, #c8d8f0)",
  },
  ".cm-panel input": {
    backgroundColor: "var(--bg-secondary, #1a1e2e)",
    color: "var(--text-primary, #c8d8f0)",
    border: "1px solid var(--control-border, rgba(255,255,255,0.1))",
    borderRadius: "4px",
  },
  ".cm-panel button": {
    backgroundColor: "var(--bg-tertiary, rgba(255,255,255,0.08))",
    color: "var(--text-primary, #c8d8f0)",
    border: "1px solid var(--control-border, rgba(255,255,255,0.1))",
    borderRadius: "4px",
  },
}, { dark: true });

// Basic extensions shared by all editor instances
function baseExtensions(filename) {
  return [
    lineNumbers(),
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history(),
    foldGutter(),
    drawSelection(),
    indentOnInput(),
    syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
    bracketMatching(),
    closeBrackets(),
    highlightActiveLine(),
    highlightSelectionMatches(),
    keymap.of([
      ...closeBracketsKeymap,
      ...defaultKeymap,
      ...searchKeymap,
      ...historyKeymap,
      ...foldKeymap,
      indentWithTab,
    ]),
    oneDark,
    polyPilotTheme,
    ...getLanguageForFile(filename),
  ];
}

// Read-only extensions
function readOnlyExtensions(filename) {
  return [
    ...baseExtensions(filename),
    EditorState.readOnly.of(true),
    EditorView.editable.of(false),
  ];
}

function resolveClickedLineNumber(target, view, event) {
  if (!(target instanceof Element)) return null;

  const gutter = target.closest('.cm-gutterElement');
  if (!gutter) return null;

  const directText = (gutter.textContent || '').trim();
  const parsed = Number.parseInt(directText, 10);
  if (Number.isInteger(parsed) && parsed > 0) {
    return parsed;
  }

  const contentRect = view.contentDOM.getBoundingClientRect();
  const gutterRect = gutter.getBoundingClientRect();
  const pos = view.posAtCoords({
    x: contentRect.left + Math.max(8, Math.min(contentRect.width - 8, 12)),
    y: event.clientY || (gutterRect.top + gutterRect.height / 2),
  });

  return pos == null ? null : view.state.doc.lineAt(pos).number;
}

function getLineClickExtensions(meta, side) {
  if (!meta?.enableLineComments || !meta.dotNetRef) return [];

  const nameForSide = side === 'original' ? (meta.oldFileName ?? meta.fileName ?? '') : (meta.fileName ?? '');

  return [EditorView.domEventHandlers({
    mousedown(event, view) {
      const lineNumber = resolveClickedLineNumber(event.target, view, event);
      if (lineNumber == null) return false;

      event.preventDefault();
      meta.dotNetRef.invokeMethodAsync(
        'HandleEditorLineClick',
        meta.fileIndex ?? -1,
        nameForSide,
        side,
        lineNumber
      ).catch(err => console.error('[PolyPilotCodeMirror] Failed to send line click to .NET', err));
      return true;
    }
  })];
}

// Global registry of active instances for cleanup
const instances = new Map();
let nextId = 1;

// --- Public API exposed to Blazor via JS interop ---

/**
 * Create a read-only code viewer with syntax highlighting and line numbers.
 * Returns an instance ID for later disposal.
 */
function createEditor(containerId, content, filename, options = {}) {
  const container = document.getElementById(containerId);
  if (!container) return -1;
  
  container.innerHTML = '';
  
  const extensions = options.editable ? baseExtensions(filename) : readOnlyExtensions(filename);
  
  const view = new EditorView({
    doc: content || '',
    extensions,
    parent: container,
  });
  
  const id = nextId++;
  instances.set(id, { type: 'editor', view, container });
  return id;
}

/**
 * Create a side-by-side diff/merge view.
 * `original` is the old content, `modified` is the new content.
 */
function createMergeView(containerId, original, modified, filename, fileIndex = -1, dotNetRef = null, enableLineComments = false, options = {}, oldFileName = null) {
  const container = document.getElementById(containerId);
  if (!container) return -1;
  
  container.innerHTML = '';
  
  const lang = getLanguageForFile(filename);
  const collapseUnchanged = options.collapseUnchanged !== false ? { margin: 3, minSize: 4 } : undefined;
  const commentMeta = {
    dotNetRef,
    fileIndex,
    fileName: filename || '',
    oldFileName: oldFileName || filename || '',
    enableLineComments,
  };
  
  // Shared visual extensions (no editability config)
  const visualExtensions = [
    lineNumbers(),
    highlightSpecialChars(),
    foldGutter(),
    drawSelection(),
    syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
    bracketMatching(),
    highlightSelectionMatches(),
    keymap.of([
      ...defaultKeymap,
      ...searchKeymap,
      ...foldKeymap,
    ]),
    oneDark,
    polyPilotTheme,
    ...lang,
  ];

  // Original (a/left) side: readonly, non-editable
  const originalExtensions = [
    ...visualExtensions,
    EditorState.readOnly.of(true),
    EditorView.editable.of(false),
  ];

  // Modified (b/right) side: fully editable with history/undo
  const modifiedExtensions = [
    ...visualExtensions,
    highlightActiveLineGutter(),
    highlightActiveLine(),
    history(),
    indentOnInput(),
    closeBrackets(),
    keymap.of([
      ...closeBracketsKeymap,
      ...historyKeymap,
      indentWithTab,
    ]),
  ];

  const mergeView = new MergeView({
    a: {
      doc: original || '',
      extensions: [...originalExtensions, ...getLineClickExtensions(commentMeta, 'original')],
    },
    b: {
      doc: modified || '',
      extensions: [...modifiedExtensions, ...getLineClickExtensions(commentMeta, 'modified')],
    },
    parent: container,
    collapseUnchanged,
    gutter: true,
  });
  
  const id = nextId++;
  instances.set(id, { type: 'merge', mergeView, container, commentMeta });
  return id;
}

/**
 * Update content of an existing editor instance.
 */
function updateContent(instanceId, content) {
  const inst = instances.get(instanceId);
  if (!inst) return;
  
  if (inst.type === 'editor') {
    inst.view.dispatch({
      changes: { from: 0, to: inst.view.state.doc.length, insert: content },
    });
  }
}

/**
 * Dispose an editor or merge view instance.
 */
function dispose(instanceId) {
  const inst = instances.get(instanceId);
  if (!inst) return;
  
  if (inst.type === 'editor') {
    inst.view.destroy();
  } else if (inst.type === 'merge') {
    inst.mergeView.destroy();
  }
  instances.delete(instanceId);
}

/**
 * Dispose all instances.
 */
function disposeAll() {
  for (const [id] of instances) {
    dispose(id);
  }
}

/**
 * Get the current content of the modified (b/right) side of a merge view.
 * Returns the edited text, or null if the instance doesn't exist or isn't a merge view.
 */
function getModifiedContent(instanceId) {
  const inst = instances.get(instanceId);
  if (!inst || inst.type !== 'merge') return null;
  return inst.mergeView.b.state.doc.toString();
}

/**
 * Open the search panel for an instance.
 */
function openSearch(instanceId) {
  const inst = instances.get(instanceId);
  if (!inst) return;
  
  if (inst.type === 'editor') {
    openSearchPanel(inst.view);
  } else if (inst.type === 'merge') {
    // Open search on the modified (b) side
    openSearchPanel(inst.mergeView.b);
  }
}

/**
 * Simulate a line-number click for testing (bypasses DOM event limitations in WebView).
 */
function simulateLineClick(instanceId, side, lineNumber) {
  const inst = instances.get(instanceId);
  if (!inst || inst.type !== 'merge' || !inst.commentMeta) return false;
  const meta = inst.commentMeta;
  if (!meta.enableLineComments || !meta.dotNetRef) return false;
  meta.dotNetRef.invokeMethodAsync(
    'HandleEditorLineClick',
    meta.fileIndex ?? -1,
    meta.fileName ?? '',
    side,
    lineNumber
  ).catch(err => console.error('[PolyPilotCodeMirror] simulateLineClick failed', err));
  return true;
}

// Expose API globally for Blazor JS interop
window.PolyPilotCodeMirror = {
  createEditor,
  createMergeView,
  updateContent,
  getModifiedContent,
  dispose,
  disposeAll,
  openSearch,
  simulateLineClick,
};
