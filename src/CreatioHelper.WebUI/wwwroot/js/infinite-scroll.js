window.creatioInfiniteScroll = (function () {
    const handles = new Map();
    let nextId = 1;

    function findScrollContainer(root) {
        if (!root) {
            return null;
        }

        return root.querySelector('.mud-table-container')
            || root.querySelector('.mud-table-body')
            || root;
    }

    return {
        attach: function (root, dotNetRef, methodName, thresholdPx) {
            const container = findScrollContainer(root);
            if (!container) {
                return 0;
            }

            const threshold = thresholdPx > 0 ? thresholdPx : 200;
            let pending = false;

            const onScroll = function () {
                if (pending) {
                    return;
                }

                const distanceToBottom =
                    container.scrollHeight - container.scrollTop - container.clientHeight;

                if (distanceToBottom > threshold) {
                    return;
                }

                pending = true;
                dotNetRef.invokeMethodAsync(methodName)
                    .finally(function () { pending = false; });
            };

            container.addEventListener('scroll', onScroll, { passive: true });

            const id = nextId++;
            handles.set(id, { container: container, onScroll: onScroll });
            return id;
        },

        detach: function (id) {
            const handle = handles.get(id);
            if (!handle) {
                return;
            }

            handle.container.removeEventListener('scroll', handle.onScroll);
            handles.delete(id);
        }
    };
})();
