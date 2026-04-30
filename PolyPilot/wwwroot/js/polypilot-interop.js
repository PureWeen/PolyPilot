// polypilot-interop.js — Named JS interop functions replacing eval-based calls.
// All functions are registered on `window` so Blazor's IJSRuntime can invoke them.

// ─── DOM Helpers ───────────────────────────────────────────────────────────────

window.setDataTheme = function (theme) {
    document.documentElement.setAttribute('data-theme', theme);
};

window.setDataPlatform = function (platform) {
    document.body.setAttribute('data-platform', platform);
};

window.setAppFontSize = function (fontSize) {
    document.documentElement.style.setProperty('--app-font-size', fontSize + 'px');
};

window.blurActiveElement = function () {
    document.activeElement?.blur();
};

window.focusAndSelect = function (elementId) {
    var el = document.getElementById(elementId);
    if (el) { el.focus(); el.select(); }
};

window.setInputValueAndCursor = function (inputId, text, cursorAtStart) {
    var el = document.getElementById(inputId);
    if (el) {
        el.value = text;
        var p = cursorAtStart ? 0 : el.value.length;
        el.setSelectionRange(p, p);
    }
};

// ─── .NET Ref Management ───────────────────────────────────────────────────────
// Pre-define the setter functions so Blazor can call them directly.

window.__setNavRef = function (ref) { window._navRef = ref; };
window.__setDashRef = function (ref) { window.__dashRef = ref; };
window.__setSettingsRef = function (ref) { window.__settingsRef = ref; };
window.__setSidebarRef = function (ref) { window.__sidebarRef = ref; };
window.__ppSetRef = function (r) { window.__ppRef = r; };

window.clearDashRef = function () { window.__dashRef = null; };
window.clearSettingsRef = function () { window.__settingsRef = null; };
window.clearSidebarRef = function () { window.__sidebarRef = null; };
window.clearPromptRef = function () { window.__ppRef = null; };

// ─── MainLayout ────────────────────────────────────────────────────────────────

window.startSidebarResize = function (startX) {
    var sidebar = document.querySelector('.sidebar.desktop-only');
    if (!sidebar) return;
    var startW = sidebar.offsetWidth;
    function onMove(e) {
        var w = Math.min(Math.max(startW + e.clientX - startX, 200), 600);
        sidebar.style.width = w + 'px';
    }
    function onUp() {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        document.body.style.userSelect = '';
        document.body.style.cursor = '';
    }
    document.body.style.userSelect = 'none';
    document.body.style.cursor = 'col-resize';
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
};

// ─── Settings ──────────────────────────────────────────────────────────────────

window.clearSettingsSearchInput = function () {
    var el = document.getElementById('settings-search');
    if (el) el.value = '';
};

window.scrollToSettingsCategory = function (category) {
    document.getElementById('settings-' + category)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
};

window.wireSettingsSearch = function () {
    var el = document.getElementById('settings-search');
    if (!el || el.__searchWired) return;
    el.__searchWired = true;
    var timer = null;
    el.addEventListener('input', function () {
        clearTimeout(timer);
        var val = el.value;
        timer = setTimeout(function () {
            if (window.__settingsRef) window.__settingsRef.invokeMethodAsync('JsUpdateSearch', val);
        }, 150);
    });

    var content = document.querySelector('article.content');
    if (content) {
        content.classList.add('settings-content-active');
        content.scrollTop = 0;
        requestAnimationFrame(function () { content.scrollTop = 0; });
    }

    var page = document.querySelector('.settings-page');
    if (page) page.scrollTop = 0;
};

window.setupCategoryIntersectionObserver = function () {
    var container = document.getElementById('settings-scroll-container');
    if (!container) return;
    var ids = ['settings-connection', 'settings-cli', 'settings-ui', 'settings-developer'];
    var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) {
            if (e.isIntersecting && window.__settingsRef) {
                var cat = e.target.id.replace('settings-', '');
                window.__settingsRef.invokeMethodAsync('JsSetActiveCategory', cat);
            }
        });
    }, { root: container, rootMargin: '-10% 0px -80% 0px', threshold: 0 });
    ids.forEach(function (id) { var el = document.getElementById(id); if (el) observer.observe(el); });
    container.__settingsObserver = observer;
};

window.removeSettingsContentActiveClass = function () {
    document.querySelector('article.content')?.classList.remove('settings-content-active');
};

// ─── Dashboard ─────────────────────────────────────────────────────────────────

window.ensureDashboardKeyHandlers = function () {
    if (window.__dashboardKeydownRegistered) return;
    window.__dashboardKeydownRegistered = true;
    document.addEventListener('keydown', function (e) {
        var sel = '.card-input input, .card-input textarea, .input-row textarea';
        var isInput = e.target.matches && e.target.matches(sel);
        if (e.key === 'Enter' && !e.shiftKey && isInput) {
            e.preventDefault();
            if (window.__sendPending) return;
            window.__sendPending = true;
            setTimeout(function () { window.__sendPending = false; }, 500);
            var container = e.target.closest('.card-input') || e.target.closest('.input-row');
            if (container) {
                var btn = container.querySelector('.send-btn:not(.stop-btn)') || container.querySelectorAll('button')[container.querySelectorAll('button').length - 1];
                if (btn) btn.click();
            }
        }
        // ArrowUp/Down: command history navigation
        if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && isInput && !e.shiftKey && !e.metaKey && !e.ctrlKey) {
            var ta = e.target;
            var atStart = ta.selectionStart === 0 && ta.selectionEnd === 0;
            var atEnd = ta.selectionStart === ta.value.length;
            var card = ta.closest('[data-session]');
            var sessionName = card ? card.dataset.session : '';
            var histNavActive = sessionName && window.__histNavActive && window.__histNavActive[sessionName];
            if ((e.key === 'ArrowUp' && atStart) || (e.key === 'ArrowDown' && (atEnd || histNavActive))) {
                if (sessionName && window.__dashRef) {
                    e.preventDefault();
                    window.__dashRef.invokeMethodAsync('JsNavigateHistory', sessionName, e.key === 'ArrowUp', ta.value || '').then(function (isNav) {
                        if (!window.__histNavActive) window.__histNavActive = {};
                        window.__histNavActive[sessionName] = isNav;
                    }).catch(function () {
                        if (window.__histNavActive) window.__histNavActive[sessionName] = false;
                    });
                }
            }
        }
        if (e.key === 'Tab' && isInput) {
            e.preventDefault();
            e.stopImmediatePropagation();
            var expandedCard = document.querySelector('.expanded-card');
            if (expandedCard && window.__dashRef) {
                window.__dashRef.invokeMethodAsync('JsCycleExpandedSession', e.shiftKey);
                return;
            }
            var inputs = Array.from(document.querySelectorAll(sel));
            if (inputs.length < 2) return;
            var idx = inputs.indexOf(e.target);
            if (idx < 0) idx = 0;
            idx = e.shiftKey ? (idx - 1 + inputs.length) % inputs.length : (idx + 1) % inputs.length;
            inputs[idx].focus();
            var card = inputs[idx].closest('.session-card');
            if (card && card.dataset.session && window.__dashRef) {
                window.__dashRef.invokeMethodAsync('JsSelectSession', card.dataset.session);
            }
        }
        // Emacs-style Ctrl keybindings for text navigation (macOS)
        if (isInput && e.ctrlKey && !e.metaKey && !e.altKey && /^mac/i.test(navigator.userAgentData?.platform ?? navigator.platform)) {
            var ta = e.target;
            var text = ta.value;
            var pos = ta.selectionStart;
            // Surrogate-pair-aware helpers
            function charLen(s, idx) {
                var code = s.charCodeAt(idx);
                return (code >= 0xD800 && code <= 0xDBFF && idx + 1 < s.length) ? 2 : 1;
            }
            function charLenBefore(s, idx) {
                if (idx <= 0) return 0;
                var code = s.charCodeAt(idx - 1);
                return (code >= 0xDC00 && code <= 0xDFFF && idx - 2 >= 0) ? 2 : 1;
            }
            if (e.key === 'a' || e.key === 'A') {
                e.preventDefault();
                var lineStart = text.lastIndexOf('\n', pos - 1) + 1;
                ta.setSelectionRange(lineStart, e.shiftKey ? ta.selectionEnd : lineStart);
                return;
            }
            if (e.key === 'e' || e.key === 'E') {
                e.preventDefault();
                var lineEnd = text.indexOf('\n', pos);
                if (lineEnd === -1) lineEnd = text.length;
                ta.setSelectionRange(e.shiftKey ? ta.selectionStart : lineEnd, lineEnd);
                return;
            }
            if (e.key === 'f' || e.key === 'F') {
                e.preventDefault();
                var np = Math.min(pos + charLen(text, pos), text.length);
                ta.setSelectionRange(e.shiftKey ? ta.selectionStart : np, np);
                return;
            }
            if (e.key === 'b' || e.key === 'B') {
                e.preventDefault();
                var np = Math.max(pos - charLenBefore(text, pos), 0);
                ta.setSelectionRange(np, e.shiftKey ? ta.selectionEnd : np);
                return;
            }
            if (e.key === 'd' && !e.shiftKey) {
                e.preventDefault();
                if (pos < text.length) {
                    var cl = charLen(text, pos);
                    ta.value = text.slice(0, pos) + text.slice(pos + cl);
                    ta.setSelectionRange(pos, pos);
                    ta.dispatchEvent(new Event('input', { bubbles: true }));
                }
                return;
            }
            if (e.key === 'h' && !e.shiftKey) {
                e.preventDefault();
                if (pos > 0) {
                    var cl = charLenBefore(text, pos);
                    ta.value = text.slice(0, pos - cl) + text.slice(pos);
                    ta.setSelectionRange(pos - cl, pos - cl);
                    ta.dispatchEvent(new Event('input', { bubbles: true }));
                }
                return;
            }
            if (e.key === 'k' && !e.shiftKey) {
                e.preventDefault();
                var lineEnd = text.indexOf('\n', pos);
                if (lineEnd === -1) lineEnd = text.length;
                if (lineEnd === pos && pos < text.length) lineEnd = pos + 1;
                ta.value = text.slice(0, pos) + text.slice(lineEnd);
                ta.setSelectionRange(pos, pos);
                ta.dispatchEvent(new Event('input', { bubbles: true }));
                return;
            }
            if (e.key === 'n' && !e.shiftKey) {
                e.preventDefault();
                var lineEnd = text.indexOf('\n', pos);
                if (lineEnd !== -1) {
                    var colStart = text.lastIndexOf('\n', pos - 1) + 1;
                    var col = pos - colStart;
                    var nextLineStart = lineEnd + 1;
                    var nextLineEnd = text.indexOf('\n', nextLineStart);
                    if (nextLineEnd === -1) nextLineEnd = text.length;
                    ta.setSelectionRange(Math.min(nextLineStart + col, nextLineEnd), Math.min(nextLineStart + col, nextLineEnd));
                }
                return;
            }
            if (e.key === 'p' && !e.shiftKey) {
                e.preventDefault();
                var colStart = text.lastIndexOf('\n', pos - 1) + 1;
                if (colStart > 0) {
                    var col = pos - colStart;
                    var prevLineEnd = colStart - 1;
                    var prevLineStart = text.lastIndexOf('\n', prevLineEnd - 1) + 1;
                    ta.setSelectionRange(Math.min(prevLineStart + col, prevLineEnd), Math.min(prevLineStart + col, prevLineEnd));
                }
                return;
            }
            if (e.key === 't' && !e.shiftKey) {
                e.preventDefault();
                var tp = pos;
                if (tp >= text.length && tp > 0) tp = tp - charLenBefore(text, tp);
                if (tp > 0 && tp < text.length) {
                    var clBefore = charLenBefore(text, tp);
                    var clAt = charLen(text, tp);
                    var charBefore = text.slice(tp - clBefore, tp);
                    var charAt = text.slice(tp, tp + clAt);
                    ta.value = text.slice(0, tp - clBefore) + charAt + charBefore + text.slice(tp + clAt);
                    ta.setSelectionRange(tp + clAt, tp + clAt);
                    ta.dispatchEvent(new Event('input', { bubbles: true }));
                }
                return;
            }
        }
        if ((e.metaKey || e.ctrlKey) && e.key === 'e') {
            e.preventDefault();
            var collapseBtn = document.querySelector('.collapse-card-btn');
            if (collapseBtn) { collapseBtn.click(); return; }
            var card = isInput ? e.target.closest('.session-card') : document.querySelector('.session-card');
            if (card && card.dataset.session && window.__dashRef) {
                window.__dashRef.invokeMethodAsync('JsExpandSession', card.dataset.session);
            }
        }
        if (e.key === 'Escape') {
            var collapseBtn = document.querySelector('.collapse-card-btn');
            if (collapseBtn) collapseBtn.click();
        }
        if ((e.metaKey || e.ctrlKey) && e.key >= '1' && e.key <= '9') {
            e.preventDefault();
            if (window.__dashRef) {
                window.__dashRef.invokeMethodAsync('JsSwitchToSessionByIndex', parseInt(e.key));
            }
        }
        if ((e.metaKey || e.ctrlKey) && (e.key === '=' || e.key === '+' || e.key === '-' || e.key === '0')) {
            e.preventDefault();
            if (window.__dashRef) {
                var delta = (e.key === '=' || e.key === '+') ? 1 : e.key === '-' ? -1 : 0;
                window.__dashRef.invokeMethodAsync('JsChangeFontSize', delta);
            }
        }
        if (e.ctrlKey && e.key === 'c' && !e.metaKey && !e.shiftKey) {
            var selection = window.getSelection();
            if (!selection || selection.toString().length === 0) {
                e.preventDefault();
                if (window.__dashRef) {
                    window.__dashRef.invokeMethodAsync('JsInterruptSession');
                }
            }
        }
    });
    // Reset histNavActive when user edits text
    document.addEventListener('input', function (e) {
        if (e.target.matches && e.target.matches('.card-input input, .card-input textarea, .input-row textarea')) {
            if (!window.__liveDrafts) window.__liveDrafts = {};
            if (e.target.id) {
                if (e.target.value) window.__liveDrafts[e.target.id] = e.target.value;
                else delete window.__liveDrafts[e.target.id];
            }
            var card = e.target.closest('[data-session]');
            var sn = card ? card.dataset.session : '';
            if (sn && window.__histNavActive) window.__histNavActive[sn] = false;
        }
    });
};

window.ensureTextareaAutoResize = function () {
    if (window.__textareaAutoResize) return;
    window.__textareaAutoResize = true;
    document.addEventListener('input', function (e) {
        if (e.target.tagName === 'TEXTAREA' && e.target.closest('.input-row')) {
            e.target.style.height = 'auto';
            e.target.style.height = Math.min(e.target.scrollHeight, 150) + 'px';
        }
    });
};

window.setDashboardScrollTop = function (scrollTop) {
    var d = document.querySelector('.dashboard');
    if (d) d.scrollTop = scrollTop;
};

window.ensureLoadMoreObserver = function () {
    if (!window.__loadMoreObserver) {
        window.__loadMoreObserver = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) entry.target.click();
            });
        }, { threshold: 0.1 });
    }
    document.querySelectorAll('.messages .load-more-btn').forEach(function (btn) {
        if (!btn.__observed) { btn.__observed = true; window.__loadMoreObserver.observe(btn); }
    });
};

window.captureDrafts = function () {
    var result = {};
    var active = document.activeElement;
    document.querySelectorAll('.card-input input, .card-input textarea, .expanded-card .input-area textarea').forEach(function (el) {
        if (el.id) result[el.id] = el.value || '';
    });
    if (active && active.id) result['__focused'] = active.id;
    if (active) { result['__selStart'] = active.selectionStart || 0; result['__selEnd'] = active.selectionEnd || 0; }
    return JSON.stringify(result);
};

window.getDashboardScrollTop = function () {
    return JSON.stringify({ top: document.querySelector('.dashboard')?.scrollTop ?? 0 });
};

window.scrollMessagesToBottom = function () {
    document.querySelectorAll('.card-messages, .messages').forEach(function (el) {
        el.scrollTop = el.scrollHeight;
    });
    setTimeout(function () {
        document.querySelectorAll('.card-messages, .messages').forEach(function (el) {
            el.scrollTop = el.scrollHeight;
        });
    }, 100);
};

window.saveDraftsAndCursor = function () {
    var focused = document.activeElement;
    var sel = '.card-input input, .card-input textarea, .input-row textarea';
    var focusId = (focused && focused.id && focused.matches(sel)) ? focused.id : null;
    var selStart = focusId ? (focused.selectionStart || 0) : 0;
    var selEnd = focusId ? (focused.selectionEnd || 0) : 0;
    var items = Array.from(document.querySelectorAll(sel))
        .filter(function (el) { return el.id; })
        .map(function (el) { return { id: el.id, value: el.value || '' }; });
    return JSON.stringify({ focusId: focusId, selStart: selStart, selEnd: selEnd, items: items });
};

// ─── Session Sidebar ───────────────────────────────────────────────────────────

window.wireSessionNameInputEnter = function () {
    var el = document.getElementById('sessionNameInput');
    if (el) {
        el.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                document.querySelector('.new-session button')?.click();
            }
        });
    }
};

window.invokeDashboardCollapseToGrid = function () {
    window.__dashRef?.invokeMethodAsync('JsCollapseToGrid');
};

// ─── DiffView ──────────────────────────────────────────────────────────────────

window.scrollAndFocusCommentBox = function () {
    setTimeout(function () {
        var el = document.querySelector('.diff-comment-box');
        if (el) {
            el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            var ta = el.querySelector('textarea');
            if (ta) ta.focus();
        }
    }, 50);
};

// ─── Expanded Session View (Popups) ────────────────────────────────────────────

// Generic popup: skills, event log
window.showPopup = function (triggerAttr, headerHtml, contentHtml, extraClass) {
    var old = document.getElementById('skills-popup-overlay');
    if (old) old.remove();
    var trigger = document.querySelector('[data-trigger="' + CSS.escape(triggerAttr) + '"]');
    var rect = trigger ? trigger.getBoundingClientRect() : { left: 20, bottom: 60, top: window.innerHeight - 60 };
    var ov = document.createElement('div');
    ov.id = 'skills-popup-overlay';
    ov.className = 'skills-popup-overlay';
    ov.onclick = function () { ov.remove(); };
    var popup = document.createElement('div');
    var maxWidth = extraClass && extraClass.indexOf('wide') >= 0 ? 468 : 368;
    var left = Math.max(8, Math.min(rect.left, window.innerWidth - maxWidth));
    var bottom = window.innerHeight - rect.top + 8;
    popup.className = 'skills-popup' + (extraClass ? ' ' + extraClass : '');
    popup.style.bottom = bottom + 'px';
    popup.style.left = left + 'px';
    popup.innerHTML = headerHtml + contentHtml;
    popup.onclick = function (e) { e.stopPropagation(); };
    ov.appendChild(popup);
    document.body.appendChild(ov);
};

// Agents popup with click-to-insert handler
window.showAgentsPopup = function (headerHtml, contentHtml, sessionName) {
    var old = document.getElementById('skills-popup-overlay');
    if (old) old.remove();
    var trigger = document.querySelector('[data-trigger="agents"]');
    var rect = trigger ? trigger.getBoundingClientRect() : { left: 20, bottom: 60, top: window.innerHeight - 60 };
    var ov = document.createElement('div');
    ov.id = 'skills-popup-overlay';
    ov.className = 'skills-popup-overlay';
    ov.onclick = function () { ov.remove(); };
    var popup = document.createElement('div');
    var left = Math.max(8, Math.min(rect.left, window.innerWidth - 368));
    var bottom = window.innerHeight - rect.top + 8;
    popup.className = 'skills-popup';
    popup.style.bottom = bottom + 'px';
    popup.style.left = left + 'px';
    popup.innerHTML = headerHtml + contentHtml;
    popup.onclick = function (e) {
        var row = e.target.closest('.agent-row');
        if (row) {
            var name = row.getAttribute('data-agent');
            ov.remove();
            var inputEl = document.querySelector('[data-session=' + JSON.stringify(sessionName) + '] textarea');
            if (inputEl) {
                inputEl.value = '@' + name + ' ';
                inputEl.dispatchEvent(new Event('input', { bubbles: true }));
                inputEl.focus();
            }
        }
    };
    ov.appendChild(popup);
    document.body.appendChild(ov);
};

// Prompts popup with full CRUD UI
window.showPromptsPopup = function (prompts, sessionName, prNumber) {
    var old = document.getElementById('skills-popup-overlay');
    if (old) old.remove();
    var trigger = document.querySelector('[data-trigger="prompts"]');
    var rect = trigger ? trigger.getBoundingClientRect() : { left: 20, bottom: 60, top: window.innerHeight - 60 };
    var ov = document.createElement('div');
    ov.id = 'skills-popup-overlay';
    ov.className = 'skills-popup-overlay';
    ov.onclick = function () { ov.remove(); };
    var popup = document.createElement('div');
    var left = Math.max(8, Math.min(rect.left, window.innerWidth - 408));
    var bottom = window.innerHeight - rect.top + 8;
    popup.className = 'skills-popup';
    popup.style.bottom = bottom + 'px';
    popup.style.left = left + 'px';
    popup.onclick = function (e) { e.stopPropagation(); };

    function renderList() {
        popup.innerHTML = '';
        var h = document.createElement('div');
        h.className = 'skills-popup-header'; h.style.cssText = 'display:flex;justify-content:space-between;align-items:center';
        var ht = document.createElement('span');
        ht.textContent = 'Prompts (click to use)';
        h.appendChild(ht);
        var newBtn = document.createElement('button');
        newBtn.textContent = '+ New';
        newBtn.style.cssText = 'background:var(--bg-tertiary);border:none;color:var(--text-bright);padding:3px 10px;border-radius:4px;cursor:pointer;font-size:var(--type-caption1)';
        newBtn.onmouseover = function () { this.style.background = 'var(--control-border)'; };
        newBtn.onmouseout = function () { this.style.background = 'var(--bg-tertiary)'; };
        newBtn.onclick = function (e) { e.stopPropagation(); renderForm('', '', '', 'new'); };
        h.appendChild(newBtn);
        popup.appendChild(h);

        if (prompts.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'skills-popup-empty';
            empty.textContent = 'No prompts yet. Click + New to create one.';
            popup.appendChild(empty);
        } else {
            prompts.forEach(function (p) {
                var row = document.createElement('div');
                row.className = 'skills-popup-row skills-popup-row--clickable';
                var top = document.createElement('div');
                top.className = 'skills-popup-row-title';
                var nameSpan = document.createElement('span');
                nameSpan.className = 'skills-popup-row-name'; nameSpan.style.cssText = 'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1';
                nameSpan.textContent = p.name;
                top.appendChild(nameSpan);
                var actions = document.createElement('span');
                actions.style.cssText = 'display:flex;align-items:center;gap:4px;margin-left:8px;flex-shrink:0';
                var srcLabel = document.createElement('span');
                srcLabel.className = 'skills-popup-row-source';
                srcLabel.textContent = p.sourceLabel;
                actions.appendChild(srcLabel);
                if (p.isUser) {
                    var editBtn = document.createElement('button');
                    editBtn.textContent = '✏️';
                    editBtn.title = 'Edit prompt';
                    editBtn.style.cssText = 'background:none;border:none;cursor:pointer;font-size:var(--type-callout);padding:2px 4px;opacity:0.7';
                    editBtn.onmouseover = function () { this.style.opacity = '1'; };
                    editBtn.onmouseout = function () { this.style.opacity = '0.7'; };
                    editBtn.onclick = function (e) { e.stopPropagation(); renderForm(p.name, p.description, p.content, 'edit'); };
                    actions.appendChild(editBtn);
                    var delBtn = document.createElement('button');
                    delBtn.textContent = '🗑️';
                    delBtn.title = 'Delete prompt';
                    delBtn.style.cssText = 'background:none;border:none;cursor:pointer;font-size:var(--type-callout);padding:2px 4px;opacity:0.7';
                    delBtn.onmouseover = function () { this.style.opacity = '1'; };
                    delBtn.onmouseout = function () { this.style.opacity = '0.7'; };
                    delBtn.onclick = function (e) {
                        e.stopPropagation();
                        if (confirm('Delete prompt "' + p.name + '"?')) {
                            if (!window.__ppRef) { ov.remove(); return; }
                            window.__ppRef.invokeMethodAsync('DeletePromptFromPopup', p.name).then(function () { ov.remove(); }).catch(function (err) { alert('Delete failed: ' + (err.message || err)); });
                        }
                    };
                    actions.appendChild(delBtn);
                }
                top.appendChild(actions);
                row.appendChild(top);
                if (p.description) {
                    var desc = document.createElement('div');
                    desc.className = 'skills-popup-row-desc';
                    desc.textContent = p.description.length > 120 ? p.description.substring(0, 117) + '…' : p.description;
                    row.appendChild(desc);
                }
                row.onclick = function (e) {
                    if (e.target.tagName === 'BUTTON') return;
                    ov.remove();
                    var inputEl = document.querySelector('[data-session=' + JSON.stringify(sessionName) + '] textarea');
                    if (inputEl) {
                        var ctx = (p.sourceLabel === 'built-in' && prNumber) ? prNumber : '';
                        inputEl.value = ctx
                            ? '/prompt use ' + p.name + ' -- ' + ctx
                            : '/prompt use ' + p.name;
                        inputEl.dispatchEvent(new Event('input', { bubbles: true }));
                        inputEl.focus();
                        inputEl.setSelectionRange(inputEl.value.length, inputEl.value.length);
                    }
                };
                popup.appendChild(row);
            });
        }
    }

    function renderForm(name, desc, content, mode) {
        popup.innerHTML = '';
        var h = document.createElement('div');
        h.style.cssText = 'padding:8px 14px;font-size:var(--type-footnote);color:var(--text-muted);border-bottom:1px solid var(--border-subtle);font-weight:600';
        h.textContent = mode === 'edit' ? 'Edit Prompt' : 'New Prompt';
        popup.appendChild(h);
        var form = document.createElement('div');
        form.style.cssText = 'padding:10px 14px';
        var mkLabel = function (text) { var l = document.createElement('div'); l.style.cssText = 'font-size:var(--type-caption1);color:var(--text-muted);margin-bottom:4px;margin-top:8px'; l.textContent = text; return l; };
        var mkInput = function (val, ph) { var i = document.createElement('input'); i.style.cssText = 'width:100%;background:var(--bg-tertiary);border:1px solid var(--control-border);border-radius:4px;padding:6px 8px;color:var(--text-bright);font-size:var(--type-callout);box-sizing:border-box'; i.value = val || ''; i.placeholder = ph; return i; };
        form.appendChild(mkLabel('Name'));
        var nameInput = mkInput(name, 'Prompt name');
        if (mode === 'edit') { nameInput.readOnly = true; nameInput.style.opacity = '0.6'; }
        form.appendChild(nameInput);
        form.appendChild(mkLabel('Description (optional)'));
        var descInput = mkInput(desc, 'Brief description');
        form.appendChild(descInput);
        form.appendChild(mkLabel('Content'));
        var contentArea = document.createElement('textarea');
        contentArea.style.cssText = 'width:100%;background:var(--bg-tertiary);border:1px solid var(--control-border);border-radius:4px;padding:6px 8px;color:var(--text-bright);font-size:var(--type-callout);min-height:100px;resize:vertical;box-sizing:border-box;font-family:inherit';
        contentArea.value = content || '';
        contentArea.placeholder = 'Prompt content...';
        form.appendChild(contentArea);
        var btns = document.createElement('div');
        btns.style.cssText = 'display:flex;justify-content:flex-end;gap:8px;margin-top:12px;padding-bottom:4px';
        var cancelBtn = document.createElement('button');
        cancelBtn.textContent = 'Cancel';
        cancelBtn.style.cssText = 'background:var(--bg-tertiary);border:none;color:var(--text-bright);padding:6px 14px;border-radius:4px;cursor:pointer;font-size:var(--type-footnote)';
        cancelBtn.onclick = function () { renderList(); };
        btns.appendChild(cancelBtn);
        var saveBtn = document.createElement('button');
        saveBtn.textContent = 'Save';
        saveBtn.style.cssText = 'background:var(--accent-primary);border:none;color:var(--bg-primary);padding:6px 14px;border-radius:4px;cursor:pointer;font-size:var(--type-footnote);font-weight:600';
        saveBtn.onclick = function () {
            var n = nameInput.value.trim();
            var c = contentArea.value.trim();
            if (!n || !c) return;
            if (!window.__ppRef) { ov.remove(); return; }
            window.__ppRef.invokeMethodAsync('SavePromptFromPopup', n, c, descInput.value.trim()).then(function () { ov.remove(); }).catch(function (err) { alert('Save failed: ' + (err.message || err)); });
        };
        btns.appendChild(saveBtn);
        form.appendChild(btns);
        popup.appendChild(form);
        if (mode !== 'edit') nameInput.focus(); else contentArea.focus();
    }

    renderList();
    ov.appendChild(popup);
    document.body.appendChild(ov);
};
