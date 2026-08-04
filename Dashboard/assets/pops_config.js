// Bu dosya kurulum sihirbazi tarafindan otomatik uretilmistir.
window.OMYO_API = {
    HTTP_URL: window.location.origin,
    WS_URL: window.location.protocol === 'https:' ? 'wss://' + window.location.host : 'ws://' + window.location.host,
    DOWNLOAD_URL: window.location.origin + '/download',
    UPDATE_URL: window.location.origin + '/updates',

    // JWT token'ini localStorage'dan veya meta tag'den al
    getToken: function() {
        return localStorage.getItem('pops_jwt') || '';
    },

    // Korumalı bir WebSocket URL'si üret (token query parametresi ile)
    wsUrl: function(path) {
        const token = this.getToken();
        const base = this.WS_URL + path;
        return token ? base + '?token=' + encodeURIComponent(token) : base;
    },

    // HTTP isteklerinde kullanılacak Authorization header'ı
    authHeader: function() {
        const token = this.getToken();
        return token ? { 'Authorization': 'Bearer ' + token } : {};
    }
};
