// Counts down from a server-rendered value. Never authoritative: the server decides when
// a hold ends, and the page reloads at zero to ask it rather than deciding for itself.
//
// A file rather than an inline block so the Content-Security-Policy can refuse inline script.
(function () {
    var box = document.querySelector('.countdown');
    if (!box) return;

    var output = box.querySelector('[data-countdown]');
    var remaining = parseInt(box.getAttribute('data-seconds'), 10);
    if (isNaN(remaining)) return;

    function pad(n) { return n < 10 ? '0' + n : String(n); }

    function render() {
        if (remaining <= 0) {
            output.textContent = '00:00:00';
            window.location.reload();
            return;
        }

        var hours = Math.floor(remaining / 3600);
        var minutes = Math.floor((remaining % 3600) / 60);
        var seconds = remaining % 60;

        output.textContent = pad(hours) + ':' + pad(minutes) + ':' + pad(seconds);
        remaining -= 1;
    }

    render();
    setInterval(render, 1000);
})();
