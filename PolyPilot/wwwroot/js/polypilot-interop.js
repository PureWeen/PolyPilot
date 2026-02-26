// PolyPilot JS interop helpers — named functions called via JS.InvokeVoidAsync.
// No eval(), no string interpolation of C# values into JS code.

window.setThemeAttribute = function(theme) {
    document.documentElement.setAttribute('data-theme', theme);
};

window.startSidebarResize = function(startX) {
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

window.__setSettingsRef = function(ref) { window.__settingsRef = ref; };

window.wireSettingsSearch = function() {
    var el = document.getElementById('settings-search');
    if (!el || el.__searchWired) return;
    el.__searchWired = true;
    var timer = null;
    el.addEventListener('input', function() {
        clearTimeout(timer);
        var val = el.value;
        timer = setTimeout(function() {
            if (window.__settingsRef) window.__settingsRef.invokeMethodAsync('JsUpdateSearch', val);
        }, 150);
    });
};

window.clearSettingsRef = function() { window.__settingsRef = null; };

window.wireSessionNameInputEnter = function() {
    var input = document.getElementById('sessionNameInput');
    if (!input || input.__enterWired) return;
    input.__enterWired = true;
    input.addEventListener('keydown', function(e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            var btn = document.querySelector('.new-session button');
            if (btn) btn.click();
        }
    });
};

window.focusAndSelect = function(elementId) {
    var el = document.getElementById(elementId);
    if (el) { el.focus(); el.select(); }
};

window.collapseToGrid = function() {
    if (window.__dashRef) window.__dashRef.invokeMethodAsync('JsCollapseToGrid');
};

window.__setDashRef = function(ref) { window.__dashRef = ref; };

window.clearDashRef = function() { window.__dashRef = null; };

window.ensureDashboardKeyHandlers = function() {
    if (window.__dashboardKeydownRegistered) return;
    window.__dashboardKeydownRegistered = true;
    document.addEventListener('keydown', function(e) {
        var sel = '.card-input input, .card-input textarea, .input-row textarea';
        var isInput = e.target.matches && e.target.matches(sel);
        if (e.key === 'Enter' && !e.shiftKey && isInput) {
            e.preventDefault();
            if (window.__sendPending) return;
            window.__sendPending = true;
            setTimeout(function() { window.__sendPending = false; }, 500);
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
                    window.__dashRef.invokeMethodAsync('JsNavigateHistory', sessionName, e.key === 'ArrowUp').then(function(isNav) {
                        if (!window.__histNavActive) window.__histNavActive = {};
                        window.__histNavActive[sessionName] = isNav;
                    }).catch(function() {
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
            var card2 = inputs[idx].closest('.session-card');
            if (card2 && card2.dataset.session && window.__dashRef) {
                window.__dashRef.invokeMethodAsync('JsSelectSession', card2.dataset.session);
            }
        }
        if ((e.metaKey || e.ctrlKey) && e.key === 'e') {
            e.preventDefault();
            var collapseBtn = document.querySelector('.collapse-card-btn');
            if (collapseBtn) { collapseBtn.click(); return; }
            var card3 = isInput ? e.target.closest('.session-card') : document.querySelector('.session-card');
            if (card3 && card3.dataset.session && window.__dashRef) {
                window.__dashRef.invokeMethodAsync('JsExpandSession', card3.dataset.session);
            }
        }
        if (e.key === 'Escape') {
            var collapseBtn2 = document.querySelector('.collapse-card-btn');
            if (collapseBtn2) collapseBtn2.click();
        }
        // ⌘1-9 / Ctrl+1-9: switch to session by index
        if ((e.metaKey || e.ctrlKey) && e.key >= '1' && e.key <= '9') {
            e.preventDefault();
            if (window.__dashRef) {
                window.__dashRef.invokeMethodAsync('JsSwitchToSessionByIndex', parseInt(e.key));
            }
        }
        // ⌘+/⌘- / Ctrl+=/Ctrl+-: font size, ⌘0 reset
        if ((e.metaKey || e.ctrlKey) && (e.key === '=' || e.key === '+' || e.key === '-' || e.key === '0')) {
            e.preventDefault();
            if (window.__dashRef) {
                var delta = (e.key === '=' || e.key === '+') ? 1 : e.key === '-' ? -1 : 0;
                window.__dashRef.invokeMethodAsync('JsChangeFontSize', delta);
            }
        }
        // Ctrl+C: interrupt running session (only when no text selected)
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
    // Reset histNavActive when user edits text, so ArrowDown doesn't
    // overwrite their changes with a history entry.
    document.addEventListener('input', function(e) {
        if (e.target.matches && e.target.matches('.card-input input, .card-input textarea, .input-row textarea')) {
            var card = e.target.closest('[data-session]');
            var sn = card ? card.dataset.session : '';
            if (sn && window.__histNavActive) window.__histNavActive[sn] = false;
        }
    });
};

window.ensureTextareaAutoResize = function() {
    if (window.__textareaAutoResize) return;
    window.__textareaAutoResize = true;
    document.addEventListener('input', function(e) {
        if (e.target.tagName === 'TEXTAREA' && e.target.closest('.input-row')) {
            e.target.style.height = 'auto';
            e.target.style.height = Math.min(e.target.scrollHeight, 150) + 'px';
        }
    });
};

window.ensureLoadMoreObserver = function() {
    if (!window.__loadMoreObserver) {
        window.__loadMoreObserver = new IntersectionObserver(function(entries) {
            entries.forEach(function(entry) {
                if (entry.isIntersecting) entry.target.click();
            });
        }, { threshold: 0.1 });
    }
    document.querySelectorAll('.messages .load-more-btn').forEach(function(btn) {
        if (!btn.__observed) { btn.__observed = true; window.__loadMoreObserver.observe(btn); }
    });
};

window.scrollMessagesToBottom = function() {
    document.querySelectorAll('.card-messages, .messages').forEach(function(el) {
        el.scrollTop = el.scrollHeight;
    });
    setTimeout(function() {
        document.querySelectorAll('.card-messages, .messages').forEach(function(el) {
            el.scrollTop = el.scrollHeight;
        });
    }, 100);
};

window.setInputValue = function(elementId, value, cursorAtStart) {
    var el = document.getElementById(elementId);
    if (el) {
        el.value = value;
        var p = cursorAtStart ? 0 : el.value.length;
        el.setSelectionRange(p, p);
    }
};

window.focusAndSetValue = function(elementId, value) {
    var el = document.getElementById(elementId);
    if (el) { el.value = value; el.focus(); }
};

window.showPopup = function(triggerSelector, headerHtml, contentHtml) {
    var old = document.getElementById('skills-popup-overlay');
    if (old) old.remove();
    var trigger = document.querySelector(triggerSelector);
    var rect = trigger ? trigger.getBoundingClientRect() : { left: 20, top: window.innerHeight - 60 };
    var ov = document.createElement('div');
    ov.id = 'skills-popup-overlay';
    ov.style.cssText = 'position:fixed;inset:0;z-index:9998;background:rgba(0,0,0,0.3)';
    ov.onclick = function() { ov.remove(); };
    var popup = document.createElement('div');
    var left = Math.max(8, Math.min(rect.left, window.innerWidth - 368));
    var bottom = window.innerHeight - rect.top + 8;
    popup.style.cssText = 'position:fixed;bottom:' + bottom + 'px;left:' + left + 'px;z-index:9999;background:#1e1e2e;border:1px solid #45475a;border-radius:10px;padding:6px 0;min-width:240px;max-width:360px;max-height:50vh;overflow-y:auto;box-shadow:0 -4px 20px rgba(0,0,0,0.5)';
    popup.innerHTML = headerHtml + contentHtml;
    popup.onclick = function(e) { e.stopPropagation(); };
    ov.appendChild(popup);
    document.body.appendChild(ov);
};

window.showPromptsPopup = function(triggerSelector, headerHtml, contentHtml, sessionName) {
    var old = document.getElementById('skills-popup-overlay');
    if (old) old.remove();
    var trigger = document.querySelector(triggerSelector);
    var rect = trigger ? trigger.getBoundingClientRect() : { left: 20, top: window.innerHeight - 60 };
    var ov = document.createElement('div');
    ov.id = 'skills-popup-overlay';
    ov.style.cssText = 'position:fixed;inset:0;z-index:9998;background:rgba(0,0,0,0.3)';
    ov.onclick = function() { ov.remove(); };
    var popup = document.createElement('div');
    var left = Math.max(8, Math.min(rect.left, window.innerWidth - 368));
    var bottom = window.innerHeight - rect.top + 8;
    popup.style.cssText = 'position:fixed;bottom:' + bottom + 'px;left:' + left + 'px;z-index:9999;background:#1e1e2e;border:1px solid #45475a;border-radius:10px;padding:6px 0;min-width:240px;max-width:360px;max-height:50vh;overflow-y:auto;box-shadow:0 -4px 20px rgba(0,0,0,0.5)';
    popup.innerHTML = headerHtml + contentHtml;
    popup.onclick = function(e) {
        e.stopPropagation();
        var row = e.target.closest('.prompt-row');
        if (row) {
            var name = row.getAttribute('data-prompt');
            ov.remove();
            var inputEl = document.querySelector('[data-session=' + CSS.escape(sessionName) + '] textarea');
            if (inputEl) {
                inputEl.value = '/prompt use ' + name;
                inputEl.dispatchEvent(new Event('input'));
            }
        }
    };
    ov.appendChild(popup);
    document.body.appendChild(ov);
};
