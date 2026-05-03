/**
 * Tab drag-to-detach functionality for multi-window viewer.
 * Enables Chrome-style tab dragging between windows.
 */
window.SteamViewerTabDrag = (function() {
    let dotNetRef = null;
    let dragState = null;
    let dropZones = [];

    /**
     * Initialize tab drag functionality.
     * @param {object} dotNetReference - Reference to the Blazor component
     */
    function initialize(dotNetReference) {
        dotNetRef = dotNetReference;

        // Add global mouse event listeners
        document.addEventListener('mousedown', handleMouseDown);
        document.addEventListener('mousemove', handleMouseMove);
        document.addEventListener('mouseup', handleMouseUp);

        console.log('[TabDrag] Initialized');
        return true;
    }

    /**
     * Cleanup tab drag functionality.
     */
    function dispose() {
        document.removeEventListener('mousedown', handleMouseDown);
        document.removeEventListener('mousemove', handleMouseMove);
        document.removeEventListener('mouseup', handleMouseUp);
        dotNetRef = null;
        dragState = null;
        console.log('[TabDrag] Disposed');
    }

    /**
     * Handle mouse down on a tab.
     */
    function handleMouseDown(e) {
        const tab = e.target.closest('.tab');
        if (!tab) return;

        // Don't start drag on close button
        if (e.target.closest('.tab-close')) return;

        const sessionId = tab.dataset.sessionId;
        if (!sessionId) return;

        // Start tracking potential drag
        dragState = {
            sessionId: sessionId,
            startX: e.clientX,
            startY: e.clientY,
            startScreenX: e.screenX,
            startScreenY: e.screenY,
            isDragging: false,
            tab: tab,
            clone: null
        };

        console.log('[TabDrag] Tracking started:', sessionId);
    }

    /**
     * Handle mouse move during potential drag.
     */
    function handleMouseMove(e) {
        if (!dragState) return;

        const dx = Math.abs(e.clientX - dragState.startX);
        const dy = Math.abs(e.clientY - dragState.startY);

        // Start drag after moving 5px
        if (!dragState.isDragging && (dx > 5 || dy > 5)) {
            startDrag(e);
        }

        if (dragState.isDragging) {
            updateDrag(e);
        }
    }

    /**
     * Start the drag operation.
     */
    function startDrag(e) {
        dragState.isDragging = true;
        dragState.tab.classList.add('dragging');

        // Create drag ghost
        const clone = dragState.tab.cloneNode(true);
        clone.classList.add('tab-drag-ghost');
        clone.style.position = 'fixed';
        clone.style.pointerEvents = 'none';
        clone.style.zIndex = '10000';
        clone.style.opacity = '0.8';
        clone.style.transform = 'scale(1.05)';
        clone.style.boxShadow = '0 4px 12px rgba(0,0,0,0.3)';
        document.body.appendChild(clone);
        dragState.clone = clone;

        updateDrag(e);

        console.log('[TabDrag] Drag started');

        // Notify Blazor
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnTabDragStart', dragState.sessionId);
        }
    }

    /**
     * Update drag position.
     */
    function updateDrag(e) {
        if (!dragState.clone) return;

        dragState.clone.style.left = (e.clientX - 60) + 'px';
        dragState.clone.style.top = (e.clientY - 15) + 'px';

        // Check if cursor is outside window bounds
        const isOutside = isOutsideWindow(e);

        if (isOutside) {
            dragState.clone.classList.add('outside-window');
        } else {
            dragState.clone.classList.remove('outside-window');
        }

        // Highlight drop zones
        updateDropZones(e);
    }

    /**
     * Check if cursor is outside the window bounds.
     */
    function isOutsideWindow(e) {
        const margin = 20;
        return (
            e.clientX < margin ||
            e.clientY < margin ||
            e.clientX > window.innerWidth - margin ||
            e.clientY > window.innerHeight - margin
        );
    }

    /**
     * Update drop zone highlighting.
     */
    function updateDropZones(e) {
        const tabBars = document.querySelectorAll('.tab-bar');

        tabBars.forEach(bar => {
            const rect = bar.getBoundingClientRect();
            const isOver = (
                e.clientX >= rect.left &&
                e.clientX <= rect.right &&
                e.clientY >= rect.top &&
                e.clientY <= rect.bottom + 20 // Extend hit area
            );

            if (isOver && bar !== dragState.tab.closest('.tab-bar')) {
                bar.classList.add('drop-target');
            } else {
                bar.classList.remove('drop-target');
            }
        });
    }

    /**
     * Handle mouse up - end drag.
     */
    function handleMouseUp(e) {
        if (!dragState) return;

        if (dragState.isDragging) {
            endDrag(e);
        }

        dragState = null;
    }

    /**
     * End the drag operation.
     */
    function endDrag(e) {
        const sessionId = dragState.sessionId;

        // Remove ghost
        if (dragState.clone) {
            dragState.clone.remove();
        }

        // Remove dragging class
        dragState.tab.classList.remove('dragging');

        // Remove drop zone highlights
        document.querySelectorAll('.tab-bar').forEach(bar => {
            bar.classList.remove('drop-target');
        });

        // Check for drop on another tab bar
        const dropTarget = document.elementFromPoint(e.clientX, e.clientY);
        const targetTabBar = dropTarget?.closest('.tab-bar');
        const sourceTabBar = dragState.tab.closest('.tab-bar');

        if (targetTabBar && targetTabBar !== sourceTabBar) {
            // Drop on different window's tab bar
            const targetWindowId = targetTabBar.dataset.windowId;
            if (targetWindowId && dotNetRef) {
                console.log('[TabDrag] Dropped on window:', targetWindowId);
                dotNetRef.invokeMethodAsync('OnTabDroppedOnWindow', sessionId, targetWindowId);
            }
        } else if (isOutsideWindow(e)) {
            // Detach to new window
            console.log('[TabDrag] Detaching tab at screen position:', e.screenX, e.screenY);
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync('OnTabDetached', sessionId, e.screenX, e.screenY);
            }
        }

        console.log('[TabDrag] Drag ended');
    }

    /**
     * Set data attribute for tab bar window ID.
     */
    function setTabBarWindowId(windowId) {
        const tabBar = document.querySelector('.tab-bar');
        if (tabBar) {
            tabBar.dataset.windowId = windowId;
        }
    }

    return {
        initialize: initialize,
        dispose: dispose,
        setTabBarWindowId: setTabBarWindowId
    };
})();
