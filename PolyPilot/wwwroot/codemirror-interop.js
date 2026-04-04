// CodeMirror 6 interop for PolyPilot
// Lazy-loads CodeMirror 6 from esm.sh CDN on first use, then caches.
// Exposes window.cmInterop with init/dispose/update functions.

(function () {
    'use strict';

    const _instances = {};
    let _loadPromise = null;
    let _cm = null;

    function _ensureLoaded() {
        if (_cm) return Promise.resolve(_cm);
        if (_loadPromise) return _loadPromise;

        _loadPromise = (async () => {
            const BASE = 'https://esm.sh/';
            const [
                cmView,
                cmState,
                cmCommands,
                cmSearch,
                cmLanguage,
                cmMerge,
                cmOneDark,
                langJS,
                langPy,
                langCSS,
                langHTML,
                langJSON,
                langMD,
                langCPP,
                langJava,
                langRust,
                langSQL,
                legacyModes,
            ] = await Promise.all([
                import(BASE + '@codemirror/view@6'),
                import(BASE + '@codemirror/state@6'),
                import(BASE + '@codemirror/commands@6'),
                import(BASE + '@codemirror/search@6'),
                import(BASE + '@codemirror/language@6'),
                import(BASE + '@codemirror/merge@6'),
                import(BASE + '@codemirror/theme-one-dark@6'),
                import(BASE + '@codemirror/lang-javascript@6'),
                import(BASE + '@codemirror/lang-python@6'),
                import(BASE + '@codemirror/lang-css@6'),
                import(BASE + '@codemirror/lang-html@6'),
                import(BASE + '@codemirror/lang-json@6'),
                import(BASE + '@codemirror/lang-markdown@6'),
                import(BASE + '@codemirror/lang-cpp@6'),
                import(BASE + '@codemirror/lang-java@6'),
                import(BASE + '@codemirror/lang-rust@6'),
                import(BASE + '@codemirror/lang-sql@6'),
                import(BASE + '@codemirror/legacy-modes@6.5.0/mode/clike'),
            ]);

            _cm = {
                // View
                EditorView:              cmView.EditorView,
                lineNumbers:             cmView.lineNumbers,
                highlightActiveLine:     cmView.highlightActiveLine,
                highlightActiveLineGutter: cmView.highlightActiveLineGutter,
                highlightSpecialChars:   cmView.highlightSpecialChars,
                drawSelection:           cmView.drawSelection,
                keymap:                  cmView.keymap,
                // State
                EditorState:             cmState.EditorState,
                Compartment:             cmState.Compartment,
                // Commands
                defaultKeymap:           cmCommands.defaultKeymap,
                historyKeymap:           cmCommands.historyKeymap,
                history:                 cmCommands.history,
                // Search
                searchKeymap:            cmSearch.searchKeymap,
                search:                  cmSearch.search,
                highlightSelectionMatches: cmSearch.highlightSelectionMatches,
                // Language
                syntaxHighlighting:      cmLanguage.syntaxHighlighting,
                defaultHighlightStyle:   cmLanguage.defaultHighlightStyle,
                foldGutter:              cmLanguage.foldGutter,
                foldKeymap:              cmLanguage.foldKeymap,
                indentOnInput:           cmLanguage.indentOnInput,
                bracketMatching:         cmLanguage.bracketMatching,
                StreamLanguage:          cmLanguage.StreamLanguage,
                // Merge / diff
                MergeView:               cmMerge.MergeView,
                // Theme
                oneDark:                 cmOneDark.oneDark,
                // Languages
                javascript:  langJS.javascript,
                python:      langPy.python,
                css:         langCSS.css,
                html:        langHTML.html,
                json:        langJSON.json,
                markdown:    langMD.markdown,
                cpp:         langCPP.cpp,
                java:        langJava.java,
                rust:        langRust.rust,
                sql:         langSQL.sql,
                // C# / Go / Swift via @codemirror/legacy-modes + StreamLanguage
                _csharp:     legacyModes.csharp,
                _go:         legacyModes.go,
                _swift:      legacyModes.swift,
            };
            return _cm;
        })();

        return _loadPromise;
    }

    function _langExtension(cm, lang) {
        if (!lang) return null;
        const l = lang.toLowerCase().replace(/^\./, '');
        switch (l) {
            case 'js': case 'mjs': case 'cjs':
                return cm.javascript();
            case 'jsx':
                return cm.javascript({ jsx: true });
            case 'ts': case 'typescript':
                return cm.javascript({ typescript: true });
            case 'tsx':
                return cm.javascript({ typescript: true, jsx: true });
            case 'py': case 'python':
                return cm.python();
            case 'css': case 'scss': case 'less':
                return cm.css();
            case 'html': case 'htm': case 'xml': case 'svg': case 'xaml': case 'razor': case 'cshtml':
                return cm.html();
            case 'json': case 'jsonc':
                return cm.json();
            case 'md': case 'markdown':
                return cm.markdown();
            case 'c': case 'h': case 'cc': case 'cpp': case 'cxx': case 'hh': case 'hpp':
                return cm.cpp();
            case 'java':
                return cm.java();
            case 'rs': case 'rust':
                return cm.rust();
            case 'sql':
                return cm.sql();
            // C# and other legacy-mode languages
            case 'cs': case 'csharp':
                return cm._csharp ? cm.StreamLanguage.define(cm._csharp) : null;
            case 'go':
                return cm._go ? cm.StreamLanguage.define(cm._go) : null;
            case 'swift':
                return cm._swift ? cm.StreamLanguage.define(cm._swift) : null;
            default:
                return null;
        }
    }

    function _viewerExtensions(cm, lang) {
        const exts = [
            cm.lineNumbers(),
            cm.highlightActiveLineGutter(),
            cm.highlightSpecialChars(),
            cm.drawSelection(),
            cm.foldGutter(),
            cm.highlightSelectionMatches(),
            cm.search({ top: true }),
            cm.syntaxHighlighting(cm.defaultHighlightStyle, { fallback: true }),
            cm.bracketMatching(),
            cm.keymap.of([
                ...(cm.defaultKeymap || []),
                ...(cm.historyKeymap || []),
                ...(cm.searchKeymap || []),
                ...(cm.foldKeymap || []),
            ]),
            cm.oneDark,
            cm.EditorView.lineWrapping,
            cm.EditorState.readOnly.of(true),
            cm.EditorView.editable.of(false),
        ];
        const langExt = _langExtension(cm, lang);
        if (langExt) exts.push(langExt);
        return exts;
    }

    window.cmInterop = {
        /** Initialize a read-only syntax-highlighted editor in elementId. */
        async initEditor(elementId, content, lang) {
            const cm = await _ensureLoaded();
            const el = document.getElementById(elementId);
            if (!el) { console.warn('[cmInterop] element not found:', elementId); return; }

            if (_instances[elementId]) {
                _instances[elementId].destroy();
                delete _instances[elementId];
            }

            _instances[elementId] = new cm.EditorView({
                state: cm.EditorState.create({
                    doc: content || '',
                    extensions: _viewerExtensions(cm, lang),
                }),
                parent: el,
            });
        },

        /** Initialize a side-by-side diff editor (original left, modified right). */
        async initDiffEditor(elementId, original, modified, lang) {
            const cm = await _ensureLoaded();
            const el = document.getElementById(elementId);
            if (!el) { console.warn('[cmInterop] element not found:', elementId); return; }

            if (_instances[elementId]) {
                _instances[elementId].destroy();
                delete _instances[elementId];
            }

            const sharedExts = [
                cm.lineNumbers(),
                cm.highlightSpecialChars(),
                cm.syntaxHighlighting(cm.defaultHighlightStyle, { fallback: true }),
                cm.bracketMatching(),
                cm.drawSelection(),
                cm.highlightSelectionMatches(),
                cm.search({ top: true }),
                cm.oneDark,
                cm.EditorView.editable.of(false),
                cm.EditorState.readOnly.of(true),
                cm.EditorView.lineWrapping,
                cm.keymap.of([
                    ...(cm.defaultKeymap || []),
                    ...(cm.searchKeymap || []),
                ]),
            ];
            const langExt = _langExtension(cm, lang);
            if (langExt) sharedExts.push(langExt);

            _instances[elementId] = new cm.MergeView({
                a: {
                    doc: original || '',
                    extensions: [...sharedExts],
                },
                b: {
                    doc: modified || '',
                    extensions: [...sharedExts],
                },
                parent: el,
                orientation: 'a-b',
                revertControls: false,
                highlightChanges: true,
                gutter: true,
                // Don't collapse unchanged — we already only have hunk context, not full file
                collapseUnchanged: undefined,
            });
        },

        /** Destroy an editor instance and free resources. */
        dispose(elementId) {
            if (_instances[elementId]) {
                _instances[elementId].destroy();
                delete _instances[elementId];
            }
        },

        /** Replace the content in an existing editor instance. */
        updateContent(elementId, content) {
            const view = _instances[elementId];
            if (!view || typeof view.dispatch !== 'function') return;
            view.dispatch({
                changes: { from: 0, to: view.state.doc.length, insert: content || '' },
            });
        },

        /** Returns true if the interop script is loaded (always true once this script runs). */
        isAvailable() { return true; },
    };
})();
