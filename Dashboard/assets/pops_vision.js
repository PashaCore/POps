// Uzak ekran (Vision v2) görüntüleyicisi: panel soketinden gelen ikili kareleri çözer ve tuvalde birleştirir.
// Panel soketindeki ikili mesaj: 0x01 · cihaz kimliğinin bayt uzunluğu (1 bayt) · cihaz kimliği (UTF-8) · ajanın
// 18 baytlık başlığı (big-endian: tür, monitör, sıra u32, x, y, w, h, tam genişlik, tam yükseklik) · JPEG.
// Koordinatlar çıktının gerçek pikselleridir; JPEG küçültülmüş olabilir ve (x, y, w, h) dikdörtgenine çizilir.
// Monitör 0xFF bütün ekranların yan yana birleştiği görüntüdür. Ayrıntı: docs/vision.md
(function () {
    'use strict';
    const FULL = 1, REGION = 2, CURSOR = 3, HEADER = 18, PANEL_FRAME = 1;
    const MAX_PENDING = 24;       // çözülmeyi bekleyen kare sınırı: tarayıcı yetişemezse bölgeler düşer, tam kare istenir
    const FULL_RETRY_MS = 3000;   // aynı çıktı için tam kare isteği en sık bu aralıkla; gelmezse bu aralıkla yinelenir
    const FULL_RETRIES = 5;       // yanıtsız kalan tam kare isteği en çok bu kadar yinelenir
    const STATS_MS = 2000;        // kare hızı ve bant genişliği bu pencereden
    const BURST_MS = 30;          // aynı yakalamanın bölgeleri art arda gelir: aralarında bundan az olanlar tek kare
    const decoder = new TextDecoder();

    // Panel soketinin ikili mesajı → kare; biçime uymazsa null
    function parse(buf) {
        if (!(buf instanceof ArrayBuffer) || buf.byteLength < 2) return null;
        const u8 = new Uint8Array(buf);
        const n = u8[1], off = 2 + n;
        if (u8[0] !== PANEL_FRAME || !n || buf.byteLength < off + HEADER) return null;
        const dv = new DataView(buf, off, HEADER);
        const f = {
            hw: decoder.decode(u8.subarray(2, off)), kind: dv.getUint8(0), monitor: dv.getUint8(1), seq: dv.getUint32(2),
            x: dv.getUint16(6), y: dv.getUint16(8), w: dv.getUint16(10), h: dv.getUint16(12),
            fw: dv.getUint16(14), fh: dv.getUint16(16), jpeg: u8.subarray(off + HEADER), bytes: buf.byteLength
        };
        if (f.kind < FULL || f.kind > CURSOR || !f.fw || !f.fh) return null;
        return f;
    }
    // Sıra numarası u32'dir ve başa döner: a, b'den önce mi
    const before = (a, b) => ((a - b) >>> 0) > 0x7fffffff;

    function drawCursor(ctx, x, y, s) {
        ctx.save();
        ctx.translate(x, y);
        ctx.scale(s, s);
        ctx.beginPath();
        ctx.moveTo(0, 0); ctx.lineTo(0, 17); ctx.lineTo(4.5, 13); ctx.lineTo(7.5, 19.5);
        ctx.lineTo(10, 18.5); ctx.lineTo(7, 12); ctx.lineTo(12.5, 12); ctx.closePath();
        ctx.fillStyle = '#fff'; ctx.strokeStyle = '#000'; ctx.lineWidth = 1.2;
        ctx.fill(); ctx.stroke();
        ctx.restore();
    }

    // canvas: görünen tuval. opts.onNeedFull(monitör): tam kare gerekiyor (sayfa select_monitor'ı yeniden gönderir);
    // opts.onFirstFrame(): ilk görüntü çizildi.
    function Viewer(canvas, opts) {
        this.canvas = canvas;
        this.ctx = canvas.getContext('2d');
        this.opts = opts || {};
        this.raf = 0;
        this.reset();
    }
    Viewer.prototype.reset = function () {
        if (this.raf) cancelAnimationFrame(this.raf);
        this.raf = 0;
        (this.retry || new Map()).forEach((t) => clearTimeout(t));
        this.retry = new Map();       // monitör -> tam kare isteğini yineleme zamanlayıcısı
        this.outs = new Map();       // monitör (0..15, 255) -> birleşik görüntü
        this.shown = null;           // en son çizilen çıktı gösterilir (ajan bir seferde tek çıktı gönderir)
        this.samples = [];           // [zaman, bayt, görüntülü mü]
        this.drawn = false;
        this.errors = 0;
        this.fullAsked = new Map();
    };
    Viewer.prototype._out = function (m) {
        let o = this.outs.get(m);
        if (!o) {
            const c = document.createElement('canvas');
            o = { canvas: c, ctx: c.getContext('2d'), fw: 0, fh: 0, base: null, gen: 0, queued: false, qfw: 0, qfh: 0,
                pending: 0, waitFull: false, cursor: null, chain: Promise.resolve() };
            this.outs.set(m, o);
        }
        return o;
    };
    // Bayt sayımı (eski JSON kareler de hız göstergesine girsin)
    Viewer.prototype.count = function (bytes, image) {
        this.samples.push([performance.now(), bytes, image !== false]);
    };
    Viewer.prototype.push = function (f) {
        this.count(f.bytes, f.kind !== CURSOR);
        const o = this._out(f.monitor);
        if (f.kind === CURSOR) {
            o.cursor = { x: f.x, y: f.y };
            if (this.shown === f.monitor) this._schedule();
            return;
        }
        if (f.kind === FULL) {
            o.gen += 1;
            o.queued = true; o.waitFull = false; o.qfw = f.fw; o.qfh = f.fh;
        } else if (!o.queued || o.waitFull || f.fw !== o.qfw || f.fh !== o.qfh || o.pending >= MAX_PENDING) {
            // Altında tam kare yok, boyut değişti ya da tarayıcı geride: bölge çizilemez, tam kare beklenir
            o.waitFull = true;
            this._needFull(f.monitor);
            return;
        }
        f.gen = o.gen;
        o.pending += 1;
        const decoded = createImageBitmap(new Blob([f.jpeg], { type: 'image/jpeg' })).catch(() => null);
        // Çözme paralel, çizim geliş sırasıyla
        o.chain = o.chain.then(() => decoded).then((bmp) => {
            o.pending -= 1;
            try { this._apply(o, f, bmp); } catch (e) { this.errors += 1; }
        });
    };
    Viewer.prototype._apply = function (o, f, bmp) {
        if (!bmp) {
            this.errors += 1;
            o.waitFull = true;
            this._needFull(f.monitor);
            return;
        }
        try {
            // Arkasından daha yeni bir tam kare geldiyse ya da bölge, altındaki tam kareden eskiyse çizilmez
            if (f.gen < o.gen || (f.kind === REGION && (o.base === null || before(f.seq, o.base)))) return;
            if (f.kind === FULL) {
                if (o.canvas.width !== f.fw || o.canvas.height !== f.fh) { o.canvas.width = f.fw; o.canvas.height = f.fh; }
                o.fw = f.fw; o.fh = f.fh; o.base = f.seq; o.retries = 0;
                o.ctx.drawImage(bmp, 0, 0, f.fw, f.fh);
            } else {
                if (f.fw !== o.fw || f.fh !== o.fh) return;
                o.ctx.drawImage(bmp, f.x, f.y, f.w, f.h);
            }
            this.shown = f.monitor;
            this._schedule();
        } finally {
            bmp.close();
        }
    };
    // force: sınırı beklemeden iste (sunucunun vision_resync'i ya da yineleme)
    Viewer.prototype._needFull = function (monitor, force) {
        const now = performance.now(), last = this.fullAsked.has(monitor) ? this.fullAsked.get(monitor) : -Infinity;
        if (!force && now - last < FULL_RETRY_MS) return;
        this.fullAsked.set(monitor, now);
        if (this.opts.onNeedFull) this.opts.onNeedFull(monitor);
        // Sunucu bayat çıktının bölgelerini tam kare gelene kadar atar: istek yanıtsız kalırsa yinelenir (yalnızca
        // gösterilen ya da henüz görüntüsü olmayan çıktı için; ekran değiştiyse eskisi için istenmez)
        clearTimeout(this.retry.get(monitor));
        this.retry.set(monitor, setTimeout(() => {
            const o = this.outs.get(monitor);
            if (!o || !o.waitFull || (this.shown !== null && this.shown !== monitor)) return;
            o.retries = (o.retries || 0) + 1;
            if (o.retries <= FULL_RETRIES) this._needFull(monitor, true);
        }, FULL_RETRY_MS));
    };
    // Sunucu bu çıktının bölgelerini düşürmeye başladı (panel yavaş): tam kare gelene kadar bölge çizilmez
    Viewer.prototype.resync = function (monitor) {
        const o = this._out(monitor);
        o.waitFull = true;
        o.retries = 0;
        this._needFull(monitor, true);
    };
    Viewer.prototype._schedule = function () {
        if (!this.raf) this.raf = requestAnimationFrame(() => this._draw());
    };
    Viewer.prototype._draw = function () {
        this.raf = 0;
        const o = this.outs.get(this.shown);
        if (!o || !o.fw) return;
        const c = this.canvas;
        if (c.width !== o.fw || c.height !== o.fh) { c.width = o.fw; c.height = o.fh; }
        this.ctx.drawImage(o.canvas, 0, 0);
        if (o.cursor) {
            // İmleç ekranda ~18 piksel boyunda kalsın (tuval küçültülerek gösterilir)
            const s = c.clientWidth && c.clientHeight ? Math.max(1, o.fw / c.clientWidth, o.fh / c.clientHeight) : 1;
            drawCursor(this.ctx, o.cursor.x, o.cursor.y, s);
        }
        if (!this.drawn) {
            this.drawn = true;
            if (this.opts.onFirstFrame) this.opts.onFirstFrame();
        }
    };
    // Gösterilen çıktının boyutu ({ width, height, monitor }); henüz görüntü yoksa null
    Viewer.prototype.output = function () {
        const o = this.outs.get(this.shown);
        return o && o.fw ? { width: o.fw, height: o.fh, monitor: this.shown } : null;
    };
    // Sayfadaki nokta → gösterilen çıktının pikseli (ajan bunu seçili ekrana ya da yan yana görüntüye eşler)
    Viewer.prototype.toRemote = function (clientX, clientY) {
        const out = this.output();
        if (!out) return null;
        const r = this.canvas.getBoundingClientRect();
        if (!r.width || !r.height) return null;
        const fit = Math.min(r.width / out.width, r.height / out.height);
        const ox = (r.width - out.width * fit) / 2, oy = (r.height - out.height * fit) / 2;
        const x = Math.round((clientX - r.left - ox) / fit), y = Math.round((clientY - r.top - oy) / fit);
        return { x: Math.max(0, Math.min(out.width - 1, x)), y: Math.max(0, Math.min(out.height - 1, y)), monitor: out.monitor };
    };
    // Son 2 saniyenin kare hızı (art arda gelen bölgeler tek kare sayılır) ve bant genişliği (kbit/sn)
    Viewer.prototype.stats = function () {
        const now = performance.now();
        while (this.samples.length && now - this.samples[0][0] > STATS_MS) this.samples.shift();
        let bytes = 0, frames = 0, last = -Infinity;
        this.samples.forEach(([t, b, image]) => {
            bytes += b;
            if (image && t - last > BURST_MS) frames += 1;
            if (image) last = t;
        });
        return { fps: Math.round(frames / (STATS_MS / 1000) * 10) / 10, kbps: Math.round(bytes * 8 / STATS_MS) };
    };
    // Gösterilen görüntünün JPEG'i (base64, öneksiz): oturum bitince önizleme olarak kalır
    Viewer.prototype.snapshot = function () {
        if (!this.drawn || !this.canvas.width) return null;
        try {
            const url = this.canvas.toDataURL('image/jpeg', 0.7);
            return url.slice(url.indexOf(',') + 1);
        } catch (e) { return null; }
    };

    window.POpsVision = { parse, Viewer, FULL, REGION, CURSOR, ALL: 255 };
})();
