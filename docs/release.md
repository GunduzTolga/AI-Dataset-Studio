# Sürüm Yayınlama ve İmzalama

AI Dataset Studio, [Velopack](https://velopack.io) ile paketlenir. `.github/workflows/release.yml` her platform için kurulum dosyalarını üretir. Bir sürüm etiketi (`v1.2.3`) gönderildiğinde bunları GitHub Releases'a yükler; kurulu uygulamalar da güncellemeleri buradan alır.

## Üretilen dosyalar

| Platform | Kanal | Kurulum | Taşınabilir |
|---|---|---|---|
| Windows x64 | `win-x64` | `AIDatasetStudio-win-x64-Setup.exe` | `AIDatasetStudio-win-x64-Portable.zip` |
| macOS Apple silicon | `osx-arm64` | `AIDatasetStudio-osx-arm64-Setup.pkg` | `AIDatasetStudio-osx-arm64-Portable.zip` (.app) |
| macOS Intel | `osx-x64` | `AIDatasetStudio-osx-x64-Setup.pkg` | `AIDatasetStudio-osx-x64-Portable.zip` (.app) |
| Linux x64 | `linux-x64` | `AIDatasetStudio-linux-x64.AppImage` | (AppImage zaten taşınabilir) |

Her kanalın yanında `releases.<kanal>.json` ve `*-full.nupkg` (bir önceki sürüm varsa `*-delta.nupkg`) dosyaları da yüklenir. Güncelleme denetimi bu dosyaları okur; silinmemeleri gerekir.

- **Paket içeriği:** Uygulama self-contained yayınlanır (.NET kurulumu gerekmez) ve ReadyToRun ile derlenir. Trimming kullanılmaz, çünkü EF Core ve Avalonia yansımaya dayanır.
- **Boyut:** Linux paketi yaklaşık 70 MB.

## Sürüm çıkarmak

1. `CHANGELOG.md` içine yeni sürümün bölümünü `## [1.2.3] - YYYY-AA-GG` başlığıyla ekleyin. Bu metin hem GitHub sürüm notu hem de kurulumda gösterilen not olur.
2. `Directory.Build.props` içindeki `<Version>` değerini yeni sürüme getirin ve `main`'e birleştirin. Yayında sürümü etiket belirler; props değeri yerel derlemeler ve PR denemeleri içindir.
3. `main` üzerindeki birleştirme commit'ini etiketleyip gönderin:
   ```bash
   git tag v1.2.3
   git push origin v1.2.3
   ```
4. İş akışı şu sırayla çalışır:
   - testleri koşturur
   - dört platformu paketler
   - Releases'ta `v1.2.3` sürümünü oluşturur, dört kanalı aynı sürüme ekler ve yayınlar
5. **Ön sürüm:** `v1.2.3-beta.1` gibi tireli etiketler GitHub'da *pre-release* olarak yayınlanır. Kararlı sürüm kullananlar bunları güncelleme olarak görmez.

**Etiketsiz deneme:** Actions › Release › *Run workflow* ile bir sürüm numarası girilir. Paketler yalnızca iş akışı çıktısı (artifact) olarak kalır, yayınlanmaz. Paketleme dosyalarını değiştiren PR'lar da aynı deneme çalışmasını otomatik başlatır.

## Güncellemeler ve özel repo

- **Varsayılan kapalı:** Uygulama varsayılan olarak hiçbir ağ isteği yapmaz. Güncelleme yalnızca kullanıcı *Yardım › Güncellemeleri Denetle*'yi seçtiğinde ya da *Açılışta Güncellemeleri Denetle* seçeneğini açtığında denetlenir. Kaynak `AppInfo.RepositoryUrl` içinde tanımlıdır.
- **Özel (private) repo:** Repo özel olduğu sürece GitHub Releases yalnızca erişim izni olanlara görünür, son kullanıcılar güncelleme alamaz. Dağıtımdan önce iki yol var:
  - reponun herkese açılması, ya da
  - sürümlerin herkese açık ayrı bir repoya yüklenmesi. Bunun için iş akışındaki `--repoUrl` ve `AppInfo.RepositoryUrl` değiştirilir; yükleme için o repoya yazma yetkisi olan bir token gerekir.
- **Geliştirme derlemeleri:** Kaynak koddan çalıştırılan kopyalar güncelleme yapmaz ve bunu durum çubuğunda söyler.

## İmzalama

Sertifika yoksa paketler **imzasız** üretilir ve yayınlanır:
- **Windows:** SmartScreen "tanınmayan uygulama" uyarısı gösterir; *Ek bilgi › Yine de çalıştır* ile açılır.
- **macOS:** Gatekeeper açmayı engeller. Uygulamaya sağ tıklayıp *Aç* denir ya da `xattr -dr com.apple.quarantine "/Applications/AI Dataset Studio.app"` çalıştırılır.
- **Linux:** AppImage'lar genelde imzalanmaz; dosyaya çalıştırma izni verilir (`chmod +x`).

İmzalama, aşağıdaki repo sırları (*Settings › Secrets and variables › Actions*) eklendiğinde kendiliğinden devreye girer.

### Windows (Authenticode)

| Sır | İçerik |
|---|---|
| `WINDOWS_CERTIFICATE` | Kod imzalama sertifikası (.pfx), base64: `base64 -w0 sertifika.pfx` |
| `WINDOWS_CERTIFICATE_PASSWORD` | .pfx parolası |

- `signtool` dosyaları SHA-256 ile imzalar ve DigiCert zaman damgası ekler.
- **Alternatif:** Azure Trusted Signing de desteklenir. Velopack `--azureTrustedSignFile` seçeneğiyle kullanılır ve iş akışına bir Azure oturum adımı eklenir.

### macOS (Developer ID + notarization)

| Sır | İçerik |
|---|---|
| `MACOS_CERTIFICATE` | *Developer ID Application* ve *Developer ID Installer* sertifikalarını içeren .p12, base64 |
| `MACOS_CERTIFICATE_PASSWORD` | .p12 parolası |
| `MACOS_SIGN_APP_IDENTITY` | Örn. `Developer ID Application: Ad Soyad (TEAMID)` |
| `MACOS_SIGN_INSTALL_IDENTITY` | Örn. `Developer ID Installer: Ad Soyad (TEAMID)` |
| `MACOS_NOTARY_APPLE_ID` | Apple ID e-postası |
| `MACOS_NOTARY_PASSWORD` | Apple ID için uygulamaya özel parola |
| `MACOS_NOTARY_TEAM_ID` | Takım kimliği (TEAMID) |

- **Akış:** İş akışı geçici bir anahtar zinciri oluşturur, sertifikaları içe aktarır ve `notarytool` kimlik bilgilerini kaydeder. Velopack .app ve .pkg dosyalarını hardened runtime ile imzalar, notarize eder ve staple'lar.
- **Entitlements:** .NET'in ihtiyaç duyduğu hardened runtime izinleri (JIT ve yerel kütüphaneler) `build/macos.entitlements` dosyasındadır.
- **Bundle kimliği:** `io.github.gunduztolga.aidatasetstudio`. Bu değer **hiç değiştirilmemelidir**; macOS uygulamayı ve verdiği izinleri bu kimlikle tanır.

## İkon

`src/AIDatasetStudio.Desktop/Assets/` içindeki ikonlar şunlardır:
- `app.ico`: Windows
- `app.icns`: macOS
- `app.png`: Linux, 512 px

Hepsi uygulamadaki marka işaretinden (degrade kare + Fluent *Sparkle*) üretilir. Markada bir değişiklik olursa ikonlar yeniden üretilip commit'lenir:

```bash
dotnet run build/make-icon.cs
```

## Yerelde paketlemek (Linux)

```bash
dotnet tool restore
dotnet publish src/AIDatasetStudio.Desktop -c Release -r linux-x64 --self-contained -p:PublishReadyToRun=true -o publish
dotnet vpk pack -u AIDatasetStudio -v 1.0.0 -p publish -e AIDatasetStudio -i src/AIDatasetStudio.Desktop/Assets/app.png -c linux-x64 -o Releases
```

`mksquashfs` gerekir (`sudo apt install squashfs-tools`).

Aynı adımlarla M6'da üretilen AppImage, Xvfb altında başlatılarak denendi. Bu deneme, açılışta sessizce kaybolan bir hatayı (kayıt listesinin ilk yüklemesi) hata günlüğü sayesinde ortaya çıkardı.
