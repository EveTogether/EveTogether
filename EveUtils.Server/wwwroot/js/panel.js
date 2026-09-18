// Keyboard and scrolling for the data lists. Up/Down step through the records and Esc closes the detail pane, by
// clicking the links the list already renders (data-dl-*). Nothing here is needed to use the panel: without this file
// every one of those links still works by mouse, touch and Tab + Enter.
(() => {
    // The URL changes as soon as a link is clicked, but the list re-renders a round trip later. A key pressed in
    // between would click the previous render's link, so keys wait in order for the render and then run.
    let rendering = false;
    const queuedKeys = [];
    let followSelection = false;
    let releaseTimer = 0;

    const isTyping = el => el instanceof Element && el.closest('input, textarea, select, [contenteditable="true"]');

    const linkFor = (list, key) => {
        if (key === 'ArrowDown') {
            return list.querySelector('[data-dl-step="next"]')
                ?? (list.querySelector('tr.sel') ? null : list.querySelector('[data-dl-row]'));
        }
        if (key === 'ArrowUp') return list.querySelector('[data-dl-step="prev"]');
        if (key === 'Escape') return list.querySelector('[data-dl-close]');
        return null;
    };

    const press = key => {
        const list = document.querySelector('[data-dl]');
        const link = list && linkFor(list, key);
        if (!link) return false;
        rendering = true;
        followSelection = key !== 'Escape';
        clearTimeout(releaseTimer);
        releaseTimer = setTimeout(() => { rendering = false; queuedKeys.length = 0; }, 2000);
        link.click();
        return true;
    };

    document.addEventListener('keydown', e => {
        if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || isTyping(e.target)) return;
        if (!['ArrowDown', 'ArrowUp', 'Escape'].includes(e.key) || !document.querySelector('[data-dl]')) return;
        if (rendering) {
            queuedKeys.push(e.key);
            e.preventDefault();
            return;
        }
        if (press(e.key)) e.preventDefault();
    });

    // Also scrolls a row another page linked to (/data#fit-12) into view; the server marks it once its data has
    // loaded, which is likewise after the navigation.
    new MutationObserver(mutations => {
        if (rendering && mutations.some(m => m.target instanceof Element && m.target.closest('[data-dl]'))) {
            rendering = false;
            clearTimeout(releaseTimer);
            const selected = document.querySelector('[data-dl] tr.sel');
            if (followSelection && selected) selected.scrollIntoView({ block: 'nearest' });
            while (queuedKeys.length > 0 && !press(queuedKeys.shift())) { /* a key with nothing to click is dropped */ }
        }
        const target = document.querySelector('tr.targeted:not([data-scrolled])');
        if (target) {
            target.setAttribute('data-scrolled', '');
            target.scrollIntoView({ block: 'center' });
        }
    }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class', 'href'] });
})();
