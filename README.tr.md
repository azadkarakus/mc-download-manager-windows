<p align="center">
  <img src="MCDownloadManager/app.ico" width="96" alt="MC Download Manager simgesi">
</p>

<h1 align="center">MC Download Manager for Windows</h1>

<p align="center">
  Windows 10/11 için hızlı, açık kaynak bir indirme yöneticisi.<br>
  Chrome, Edge, Brave ve Firefox indirmelerini yakalar, 16'ya kadar paralel bağlantıyla hızlandırır ve web sayfalarındaki videoları indirir.
</p>

<p align="center">
  <a href="README.md">🇬🇧 English</a> · <a href="https://github.com/azadkarakus/mc-download-manager-windows/releases">Sürümler</a> · <a href="#kaynaktan-derleme">Derleme</a>
</p>

---

Bu proje, Mesut Çevik'in macOS için geliştirdiği [MC Download Manager](https://github.com/mstcvk/mc-download-manager) uygulamasının Windows sürümüdür.
C# / WPF ile ayrıca yazılmıştır, ama aynı tarayıcı uzantısını ve aynı yerel köprü API'sini kullanır. Bu yüzden iki uygulama aynı şekilde çalışır.

Arayüz **Türkçe** ve **İngilizce**dir (Windows görüntüleme diline göre seçilir) ve sistemin açık/koyu temasını izler.

## Özellikler

- **Çok bağlantılı indirme:** Dosyalar HTTP `Range` isteklerine bölünür (varsayılan 8 bağlantı, 1–16 arası ayarlanabilir).
  Sunucu Range desteklemiyorsa tek akışa geçer, `429`/`503` ile yavaşlatıldığında bağlantı sayısını düşürür.
- **Duraklat / devam et:** Parça durumu diske kaydedilir, indirmeler yeniden başlatmadan sonra bile kaldığı yerden devam eder.
- **Tarayıcı entegrasyonu:** Tarayıcının indirmesi iptal edilir ve çerezler, `Referer` ve `User-Agent` ile birlikte uygulamaya aktarılır.
  Hangi dosya türlerinin yakalanacağı ayarlanabilir.
- **Video indirme:** Sayfadaki `<video>`, `.m3u8` ve `.mpd` akışları algılanır. "Bu sayfadaki videoyu indir" seçeneği, ffmpeg ile birleştirme yapan yt-dlp'yi kullanır.
  Varsayılan çıktı 1080p'ye kadar MP4'tür; kapsayıcı ve kalite ayarlanabilir.
- **Pano:** Bir bağlantı kopyaladığınızda uygulama indirmek isteyip istemediğinizi sorar. Ana pencerede Ctrl+V bağlantı yapıştırır, Ctrl+N yeni indirme ekler.
- **Windows arayüzü:** Windows 11 (Fluent) tasarımı, kenar çubuğunda kategoriler, aranabilir ve sıralanabilir liste,
  her indirme için parça çubuklu ayrıntı penceresi, sistem tepsisi simgesi ve bildirimler.
- **Sistem tepsisinde çalışır:** İsterseniz Windows ile başlar; böylece uygulamayı açmadan tarayıcı indirmeleri yakalanır.
  Pencereyi kapatmak uygulamayı kapatmaz; çıkmak için tepsi simgesini kullanın.

## Kurulum

> İmzalı kurulum dosyaları [Sürümler](https://github.com/azadkarakus/mc-download-manager-windows/releases) sayfasında yayınlanacak. O zamana kadar [kaynaktan derleyebilirsiniz](#kaynaktan-derleme).

### Tarayıcı uzantısı

Uygulama, her açılışta uzantıyı `%LOCALAPPDATA%\MC Download Manager\BrowserExtension` klasörüne kopyalar (*Ayarlar ▸ Tarayıcı* klasörü gösterir).

- **Chrome / Edge / Brave:** `chrome://extensions` adresini açın (Edge'de `edge://extensions`), *Geliştirici modu*nu açın, *Paketlenmemiş öğe yükle*'yi seçip bu klasörü gösterin.
- **Firefox:** *about:debugging ▸ Bu Firefox ▸ Geçici Eklenti Yükle* ile klasördeki `manifest.json` dosyasını seçin.
  Mozilla tarafından imzalanmış sürüm çıkana kadar bu yükleme geçicidir.

### Video araçları

yt-dlp, ffmpeg ve deno (YouTube için gerekli) pakete **dahil değildir**. İlk video indirmesinde uygulama, bunları resmi GitHub sürümlerinden kurmayı önerir
([yt-dlp](https://github.com/yt-dlp/yt-dlp), [yt-dlp/FFmpeg-Builds](https://github.com/yt-dlp/FFmpeg-Builds), [deno](https://github.com/denoland/deno)).
Her dosyanın SHA-256 sağlama toplamı kullanılmadan önce doğrulanır. Araçları *Ayarlar ▸ Video* bölümünden güncelleyebilirsiniz.

## Güvenlik

- Köprü yalnızca `127.0.0.1:48213` adresini dinler ve yalnızca tarayıcı uzantısı kaynaklarından (`chrome-extension://`, `moz-extension://`)
  ve loopback `Host` ile gelen isteklere cevap verir. Normal web sayfalarından gelen istekler, `Origin: null` dahil, reddedilir.
- Çerezler ve `Authorization` başlığı yalnızca indirmenin başlatıldığı sunucuya gönderilir, yönlendirmeyle ulaşılan başka bir sunucuya asla gönderilmez.
- İndirilen dosyalara tarayıcı indirmelerinde olduğu gibi *Mark of the Web* (`Zone.Identifier`) işareti eklenir; böylece SmartScreen ve Office dosyaları yine denetler.
- Ayarlar, indirme listesi ve çerez dosyaları `%LOCALAPPDATA%\MC Download Manager\` klasöründe tutulur ve yalnızca sizin Windows kullanıcınız tarafından okunabilir.

## Kaynaktan derleme

Gereksinimler: Windows 10/11 ve [.NET 10 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.10`).

```powershell
dotnet build MCDownloadManager                               # hata ayıklama derlemesi
dotnet run --project Tests                                   # indirme motoru ve köprü testleri
dotnet publish MCDownloadManager -c Release -o publish       # publish\ klasörüne bağımsız derleme
```

## Proje yapısı

```
MCDownloadManager/
  SegmentedDownloader.cs   çok bağlantılı HTTP motoru (Range, uyarlanabilir bağlantı sayısı, IPv4/IPv6 yarışı)
  DownloadStore.cs         kuyruk, kalıcılık, hız ölçümü, yt-dlp işleri
  Tools.cs                 yt-dlp sarmalayıcısı ve araç kurucu (sağlama toplamı doğrulama)
  BridgeServer.cs          tarayıcı uzantısı için yerel HTTP köprüsü
  App.xaml.cs              tepsi simgesi, tek örnek, pano, Windows ile başlatma
  MainWindow / DetailWindow / SettingsWindow
Tests/                     yerel test sunucusuna karşı testler
Extension/                 WebExtension (manifest v3), macOS uygulamasıyla ortak
```

### Köprü API'si

| Uç nokta | Açıklama |
|---|---|
| `GET /ping` | sürüm, yt-dlp durumu, etkin indirme sayısı, yakalanan dosya türleri |
| `POST /download` | `{url, filename?, referer?, userAgent?, cookieHeader?, cookies?[], kind: "file"\|"video", segments?}` |
| `GET /downloads` | son 50 indirmenin durumu |
| `POST /action` | `{id, action: "pause"\|"resume"\|"cancel"\|"remove"}` |

API, macOS uygulamasınınkiyle aynıdır; bu sayede aynı uzantı ikisiyle de çalışır.

## Katkı

Hata bildirimleri ve pull request'ler memnuniyetle karşılanır. Metinler `Models.cs` (`S` sınıfı) ve `Extension/_locales/` içindedir.

## Teşekkür ve lisans

Mesut Çevik'in [mstcvk/mc-download-manager](https://github.com/mstcvk/mc-download-manager) projesine dayanır. Tarayıcı uzantısı ve köprü API'si o projeden gelir.

Kod MIT Lisansı ile lisanslanmıştır (bkz. `LICENSE`). yt-dlp (Unlicense), FFmpeg (yt-dlp/FFmpeg-Builds GPL derlemesi) ve deno (MIT)
çalışma sırasında indirilir ve bu deponun parçası değildir.
