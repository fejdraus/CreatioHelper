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
        attach: function (root, dotNetRef, methodName, screensAhead) {
            const container = findScrollContainer(root);
            if (!container) {
                return 0;
            }

            const screens = screensAhead > 0 ? screensAhead : 1.5;
            let pending = false;

            const request = function () {
                if (pending) {
                    return;
                }

                pending = true;
                dotNetRef.invokeMethodAsync(methodName)
                    .then(function (moreMayRemain) {
                        pending = false;
                        // A page that did not fill the runway leaves the reader one
                        // scroll away from the end again, so keep going - but only
                        // while the component says there is more to fetch, otherwise
                        // this would spin on an exhausted list.
                        if (moreMayRemain) {
                            setTimeout(check, 0);
                        }
                    })
                    .catch(function () { pending = false; });
            };

            const check = function () {
                const distanceToBottom =
                    container.scrollHeight - container.scrollTop - container.clientHeight;

                // Start fetching while the reader still has this much left to
                // scroll, so the next page is already in place on arrival.
                if (distanceToBottom <= container.clientHeight * screens) {
                    request();
                }
            };

            container.addEventListener('scroll', check, { passive: true });
            setTimeout(check, 0);

            const id = nextId++;
            handles.set(id, { container: container, onScroll: check });
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
