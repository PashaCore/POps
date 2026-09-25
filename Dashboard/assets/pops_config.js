// Bu dosya kurulum sihirbazi tarafindan otomatik uretilmistir.
window.OMYO_API = {
    HTTP_URL: window.location.origin,
    WS_URL: window.location.protocol === 'https:' ? 'wss://' + window.location.host : 'ws://' + window.location.host,
    DOWNLOAD_URL: window.location.origin + '/download',
    UPDATE_URL: window.location.origin + '/updates',

    // JWT httpOnly çerezde taşınır; tarayıcı onu HTTP ve WebSocket isteklerine kendisi ekler.
    // Token URL'ye yazılmaz ve JavaScript tarafından okunamaz.
    wsUrl: function(path) {
        return this.WS_URL + path;
    }
};
