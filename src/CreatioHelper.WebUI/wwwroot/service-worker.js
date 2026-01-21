// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// temporary change the URL and tell the browser to reload).
self.addEventListener('fetch', () => { });
