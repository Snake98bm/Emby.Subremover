// Emby only lists old-style plugin pages in the server sidebar, so this page just forwards to the SubRemover
// settings page. location.replace keeps it out of the history, so Back doesn't land here again.
define([], function () {
    'use strict';

    var target = 'genericui?PageId=' + encodeURIComponent('82033f:SubremoverMenu');

    return function (view) {
        view.addEventListener('viewshow', function () {
            var base = window.location.href.split('#')[0];
            window.location.replace(base + '#!/' + target);
        });
    };
});
