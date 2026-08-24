window.creatioDownload = {
    saveFile: function (fileName, contentType, base64) {
        const link = document.createElement('a');
        link.href = 'data:' + contentType + ';base64,' + base64;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    }
};

window.creatioBrowser = {
    prefersDarkColorScheme: function () {
        return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    }
};

window.creatioNotifications = {
    permission: function () {
        return (typeof Notification === 'undefined') ? 'unsupported' : Notification.permission;
    },
    requestPermission: async function () {
        if (typeof Notification === 'undefined') {
            return 'unsupported';
        }
        try {
            return await Notification.requestPermission();
        } catch (e) {
            return 'denied';
        }
    },
    show: function (title, body, tag) {
        if (typeof Notification === 'undefined' || Notification.permission !== 'granted') {
            return false;
        }
        try {
            new Notification(title, { body: body || '', tag: tag || '' });
            return true;
        } catch (e) {
            return false;
        }
    }
};
