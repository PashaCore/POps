"""Sınıflar (laboratuvarlar) için ortak sabitler.

UNASSIGNED_LAB, sınıfa atanmamış cihazın clients.lab_name değeridir. Adı bitirme projesinden kalmadır ama
veritabanında saklanan ve API'de olduğu gibi görünen bir değerdir: cihaz listeleri, /api/v1, CSV raporları,
görevlerin target_lab'ı, modül ayarlarının scope_id'si ve zamanlanmış görev hedefleri onu taşır; ajan da kalp
atışında gönderir (sunucu okumaz). Yeniden adlandırmak bu değerlerin hepsini taşıyan bir göç ve API kullananlar için
kırıcı bir değişiklik olurdu; bu yüzden değer aynı kalır, yalnızca tek yerde tanımlıdır. Paneldeki karşılığı
Dashboard/assets/pops_devices.js'te POps.dev.UNASSIGNED; ikisi birlikte değişir.
"""

UNASSIGNED_LAB = "Atanmamis_Cihazlar"
