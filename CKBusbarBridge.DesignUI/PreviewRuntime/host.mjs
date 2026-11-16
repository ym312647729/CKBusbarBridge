// No visible rendering surface. Geometry is returned to the native SW OpenGL owner.
const worker = new Worker('./session-worker.mjs', {type:'module'});
worker.onmessage = event => chrome.webview.postMessage(event.data);
worker.onerror = event => chrome.webview.postMessage({type:'fatal',ok:false,error:event.message});
chrome.webview.addEventListener('message', event => worker.postMessage(event.data));
