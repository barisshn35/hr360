# İşletim sistemine göre kurulum

HR360 her yerde aynı yolla kurulur: Docker Engine ve Docker Compose v2 hazırlanır, repo klonlanır
ve `./install.sh` çalıştırılır. İşletim sistemleri arasında yalnızca şunlar değişir:

- Docker'ın nasıl kurulduğu
- Firewall'un nasıl ayarlandığı
- Masaüstü sistemlerde Docker Desktop'a ne kadar bellek verildiği

`install.sh` Linux'ta Docker'ı kendisi kurmayı teklif eder. Bunun için `/etc/os-release` dosyasından
dağıtımı tanır ve Docker'ın resmi deposunu ya da dağıtımın kendi paketini kullanır. macOS ve
Windows'ta Docker Desktop'ı sizin kurmanız gerekir; betik bunu söyleyip durur.

## Kısa tablo

| İşletim sistemi | Docker'ı kim kurar | Docker kaynağı | Durum |
|---|---|---|---|
| Ubuntu 22.04 / 24.04 | `install.sh` | download.docker.com (apt) | Docker kurulumu ve uygulamanın tamamı denendi |
| Debian 12 / 13 | `install.sh` | download.docker.com (apt) | Docker kurulumu denendi |
| Linux Mint, Pop!_OS ve diğer Ubuntu/Debian türevleri | `install.sh` | Bağlı olduğu Ubuntu/Debian deposu | Dağıtım tanıma denendi |
| RHEL 8 / 9 | `install.sh` | download.docker.com (`rhel` deposu) | — |
| Rocky Linux, AlmaLinux, Oracle Linux, CentOS Stream 9 | `install.sh` | download.docker.com (`centos` deposu) | Docker kurulumu denendi |
| Fedora | `install.sh` | download.docker.com (dnf) | Docker kurulumu denendi |
| Amazon Linux 2023 / 2 | `install.sh` | Amazon'un `docker` paketi + resmi Compose eklentisi | Docker kurulumu denendi (2023) |
| openSUSE Leap / Tumbleweed, SLES | `install.sh` | Dağıtımın `docker` ve `docker-compose` paketleri | Docker kurulumu denendi (Leap 15.6) |
| Arch Linux, Manjaro | `install.sh` | pacman | Docker kurulumu denendi |
| Alpine | `install.sh` | apk (OpenRC) | Docker kurulumu denendi |
| Windows 10 / 11 | Siz (Docker Desktop) ya da WSL içinde `install.sh` | WSL2 | — |
| macOS (Intel / Apple Silicon) | Siz (Docker Desktop) | — | — |
| Windows Server | Desteklenmez | — | Windows container'ları değil Linux container'ları gerekir; bir Linux VM kullanın |

"Docker kurulumu denendi": Docker'ı kuran adım ilgili dağıtımın container imajında çalıştırıldı;
Docker Engine ve Compose eklentisinin kurulduğu doğrulandı. Uygulamanın tamamı Ubuntu 24.04 üzerinde
kuruldu ve test edildi. "—" işaretli satırlar bu ortamda denenmedi.

## Ortak gereksinimler

| | En az | Önerilen |
|---|---|---|
| CPU | 4 çekirdek | 6–8 çekirdek |
| Bellek (Docker'ın kullanabildiği) | 8 GB | 16 GB |
| Boş disk (repo'nun bulunduğu bölümde) | 40 GB | 80 GB |
| Mimari | x86_64 (amd64) | — |

- **ARM sunucular** (AWS Graviton, Raspberry Pi 5, Apple Silicon): Docker kurulumu desteklenir, ancak
  bütün imajların ARM sürümüyle uygulama derlenip denenmedi.
- **Gerekli araçlar:** `install.sh` `curl` ve `openssl` kullanır; Linux'ta eksikse kendisi kurar.
  Repoyu klonlamak için `git` gerekir.
- **Sudo:** Root dışındaki kullanıcı için `sudo` yetkisi gerekir. Root olarak, `sudo` kurulu olmayan
  sistemlerde de çalışır.

---

## Ubuntu / Debian (ve türevleri)

```bash
sudo apt-get update && sudo apt-get install -y git
git clone https://github.com/barisshn35/hr360.git && cd hr360
./install.sh
```

Docker yoksa `install.sh` onay ister ve Docker'ın apt deposunu ekleyip Engine ile Compose eklentisini
kurar. Kullanıcınızı `docker` grubuna ekler; kurulumun geri kalanında geçici olarak `sudo docker`
kullanır.

Firewall (`ufw`):

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
```

> **Önemli:** Docker, yayınladığı portlar için kendi iptables kurallarını yazar ve bu kurallar
> `ufw`'nin önüne geçer. Kurulum veri servislerini zaten yalnızca `127.0.0.1`'e açar, yani etkilenmezler.
> Ama Keycloak panelini ayrı porta (8090) aldıysanız o portu `ufw` ile kapatamazsınız. Ya `DOCKER-USER`
> zinciri ya da bulut firewall'u kullanın; örnekler [Keycloak paneli runbook'unda](../runbooks/keycloak-yonetim-paneli-erisimi.md).

## RHEL, Rocky Linux, AlmaLinux, Oracle Linux, CentOS Stream

```bash
sudo dnf install -y git
git clone https://github.com/barisshn35/hr360.git && cd hr360
./install.sh
```

- **Docker kaynağı:** `install.sh` Docker'ın `docker-ce.repo` deposunu ekler (RHEL'de `rhel`, diğerlerinde
  `centos` deposu) ve `docker-ce` paketlerini kurar.
- **Podman çakışması:** Sistemde Podman'ın `podman-docker` ya da `runc` paketleri varsa
  `--allowerasing` bunları Docker'ınkilerle değiştirir. Podman'ın kendisi kalır, ama HR360'ı Podman ile
  değil Docker ile çalıştırın.
- **RHEL aboneliği:** RHEL'de abonelik etkin olmalıdır, çünkü bağımlılıklar RHEL depolarından gelir.
- **SELinux:** `enforcing` modunda kalabilir. Docker'ın kendi paketi SELinux etiketlemesini varsayılan
  olarak kapalı çalıştırır, bu yüzden repo içindeki yapılandırma dosyaları container'lara sorunsuz
  bağlanır. `/etc/docker/daemon.json` dosyasında `"selinux-enabled": true` ayarını kendiniz açtıysanız
  kapatın.

Firewall (`firewalld`):

```bash
sudo firewall-cmd --permanent --add-service=http --add-service=https
sudo firewall-cmd --reload
```

Docker, `firewalld` ile birlikte kendi `docker` bölgesini kullanır. Yayınlanan portlar için
yukarıdaki `ufw` notu burada da geçerlidir.

## Fedora

RHEL ile aynı adımlar (`sudo dnf install -y git`, ardından `./install.sh`). Docker'ın Fedora deposu
kullanılır. Fedora'nın kendi `moby-engine` paketi kuruluysa önce kaldırın:
`sudo dnf remove moby-engine`. Bu paket SELinux etiketlemesini açık getirir.

## Amazon Linux 2023 / Amazon Linux 2

```bash
sudo dnf install -y git        # Amazon Linux 2: sudo yum install -y git
git clone https://github.com/barisshn35/hr360.git && cd hr360
./install.sh
```

- **Docker kaynağı:** Amazon'un kendi `docker` paketi kurulur. Bu paket Compose eklentisi içermediği
  için `install.sh` resmi Compose ikilisini `/usr/local/lib/docker/cli-plugins/` altına indirir.
- **Firewall:** Sunucuda firewall yoktur; erişim **Security Group** ile yönetilir. Gelen kurallarda
  yalnızca 80 ve 443 herkese, 22 yalnızca kendi IP'nize açık olsun.
- **Disk:** EBS diskini en az 40 GB yapın. Varsayılan 8 GB yetmez.

## openSUSE Leap / Tumbleweed, SLES

```bash
sudo zypper -n install git
git clone https://github.com/barisshn35/hr360.git && cd hr360
./install.sh
```

- **Docker kaynağı:** Dağıtımın `docker` ve `docker-compose` paketleri kurulur. Compose eklentisi
  çalışmazsa resmi ikili indirilir.
- **SLES:** Önce Containers modülünü açın:
  `sudo SUSEConnect -p sle-module-containers/15.6/x86_64` (sürüm ve mimariyi kendinize göre yazın).

Firewall (`firewalld`):
`sudo firewall-cmd --permanent --add-service=http --add-service=https && sudo firewall-cmd --reload`

## Arch Linux / Manjaro

```bash
sudo pacman -Sy --needed git
git clone https://github.com/barisshn35/hr360.git && cd hr360
./install.sh
```

`install.sh` `docker`, `docker-compose` ve `docker-buildx` paketlerini kurar. Sürekli güncellenen bir
dağıtım olduğu için üretim sunucusu olarak LTS dağıtımları öneririz.

## Alpine

```bash
apk add git bash
git clone https://github.com/barisshn35/hr360.git && cd hr360
bash install.sh
```

`install.sh` `docker` ve `docker-cli-compose` paketlerini kurar ve servisi OpenRC'ye ekler. Varsayılan
kabuk `ash` olduğu için betiği `bash` ile çalıştırın.

---

## Windows 10 / 11 (WSL2)

Betikler Linux'ta çalışır. Windows'ta **WSL2** içindeki bir Linux dağıtımında (önerilen: Ubuntu 24.04)
çalıştırılır. PowerShell ya da CMD'den doğrudan çalışmaz.

1. **WSL2 ve Ubuntu'yu kurun.** Yönetici PowerShell'de:
   ```powershell
   wsl --install -d Ubuntu-24.04
   ```
   Bilgisayarı yeniden başlatın ve Ubuntu'yu açıp kullanıcı oluşturun.
2. **WSL'e yeterli bellek verin.** `%UserProfile%\.wslconfig` dosyasına yazın:
   ```ini
   [wsl2]
   memory=12GB
   processors=6
   ```
   Ardından PowerShell'de `wsl --shutdown` çalıştırın.
3. **Docker'ı hazırlayın.** İki yoldan birini seçin:
   - **Docker Desktop (önerilen):** Windows'a Docker Desktop kurun. Settings > Resources >
     WSL Integration'da Ubuntu'yu açın.
   - **WSL içinde Docker Engine:** Ubuntu'da `/etc/wsl.conf` dosyasına aşağıdakini yazın, PowerShell'de
     `wsl --shutdown` çalıştırın ve Ubuntu'yu yeniden açın. Bundan sonra `install.sh` Docker'ı Ubuntu
     gibi kurar.
     ```ini
     [boot]
     systemd=true
     ```
4. **Repoyu WSL'in kendi diskine klonlayın** (`/mnt/c/...` altına değil). Windows diskinde build çok
   yavaştır. Ubuntu terminalinde:
   ```bash
   sudo apt-get update && sudo apt-get install -y git
   cd ~ && git clone https://github.com/barisshn35/hr360.git && cd hr360
   ./install.sh
   ```
5. **Uygulamayı açın.** Windows tarayıcısında `http://localhost` adresini açın. Kurulumda adres
   sorusunu varsayılan bırakın.

Repoyu Windows tarafında klonlayıp WSL'e kopyaladıysanız satır sonları CRLF olabilir. Bu durumda
betik `$'\r': command not found` gibi hatalarla durur; repoyu WSL içinde yeniden klonlayın. Repodaki
`.gitattributes` dosyası, Git ile klonlanan betikleri her zaman LF tutar.

## macOS (Intel ve Apple Silicon)

1. [Docker Desktop](https://www.docker.com/products/docker-desktop/) kurun ve açın.
2. Docker Desktop > Settings > Resources'ta belleği **en az 8 GB** (önerilen 12 GB), diski
   **en az 40 GB** yapın.
3. Terminalde:
   ```bash
   xcode-select --install        # git yoksa
   git clone https://github.com/barisshn35/hr360.git && cd hr360
   ./install.sh
   ```
4. Tarayıcıda `http://localhost` adresini açın.

- Betikler macOS'un kendi bash'i (3.2) ve BSD araçlarıyla (`sed`, `wc`) uyumlu olacak şekilde
  yazıldı, ancak macOS'ta denenmedi.
- Apple Silicon'da imajlar ARM için derlenir; bu ortamda denenmedi. Sorun çıkarsa Docker Desktop'ta
  "Use Rosetta for x86/amd64 emulation" seçeneğini açıp `DOCKER_DEFAULT_PLATFORM=linux/amd64 ./install.sh`
  ile x86 imajlarıyla kurabilirsiniz (daha yavaştır).
- macOS ve Windows yalnızca geliştirme ve deneme içindir. Let's Encrypt ve dışarıya açık kurulum için
  Linux sunucu kullanın.

---

## Bulut ve sanallaştırma ortamları

- **AWS / Azure / GCP:** Firewall Security Group, NSG ya da VPC firewall kurallarıyla yönetilir:
  80 ve 443 herkese, 22 yalnızca yönetim IP'lerine açık olsun. Disk en az 40 GB olsun.
- **VMware vCloud Director (NSX-V Edge):** VM'e genel IP verilmez. Edge'de şunları ayarlayın:
  - Genel IP'nin 80 ve 443 portları için VM'in iç IP'sine DNAT ve bu portlara izin veren firewall kuralı.
  - VM'in internete çıkabilmesi için SNAT; Docker imajları, Let's Encrypt ve SMTP buna ihtiyaç duyar.
  - Edge yük dengeleyicisini TLS sonlandıracak şekilde kurmayın.
    Dengeleyici kullanacaksanız: `scripts/tls.sh external` ([HTTPS runbook'u](../runbooks/https.md)).
- **Sunucunun kendi posta servisi (`--mail-mode self`, isteğe bağlı):** çoğu bulut sağlayıcısı
  25/tcp çıkışını varsayılan olarak engeller (AWS, Azure, GCP, Hetzner, DigitalOcean); destek
  talebiyle açtırın ve IP'nin PTR kaydını sağlayıcı panelinden alan adınıza yönlendirin. vCloud'da
  SNAT kuralı 25/tcp çıkışına da izin vermeli. Ayrıntı: [işletim kılavuzu](../isletim/README.md#sunucunun-kendi-posta-servisi).
- **Proxmox / KVM / Hyper-V:** Bir Linux VM oluşturup yukarıdaki dağıtım adımlarını izleyin.

## Kurulumdan sonra

Bütün sistemlerde aynıdır; kök [README](../../README.md) dosyasındaki
"İlk giriş ve ilk şirket" bölümüne bakın.
