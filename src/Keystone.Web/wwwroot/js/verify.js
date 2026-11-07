// Hands the sign-in token back to the server.
//
// The emailed link carries it in the fragment — after the '#' — which a browser never sends
// to a server. So the GET that loaded this page carried no credential: nothing for the request
// log, a proxy in between, or browser history to keep. This posts it instead, which is also
// why a mail scanner following the link cannot spend it: scanners do not run script and do not
// post forms.
//
// A file rather than an inline block so the Content-Security-Policy can refuse inline script.
(function () {
    var form = document.getElementById('verify-form');
    var field = document.getElementById('verify-token');
    var status = document.getElementById('verify-status');
    if (!form || !field) return;

    var prefix = '#token=';
    var hash = window.location.hash;

    if (hash.indexOf(prefix) !== 0 || hash.length === prefix.length) {
        status.textContent = 'That link is missing its sign-in token. Ask for a new one.';
        return;
    }

    field.value = hash.substring(prefix.length);

    // Out of the address bar, so it is not left on screen or carried into a copied URL.
    history.replaceState(null, '', window.location.pathname);

    form.submit();
})();
