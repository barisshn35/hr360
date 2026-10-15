#!/bin/sh
# HR360 posta servisi: Postfix (yalnızca giden) + OpenDKIM.
#   MAIL_HOSTNAME  Sunucunun tam adı (EHLO); IP'nin PTR kaydı bu adı göstermeli.
#   MAIL_DOMAIN    Gönderen adresin alan adı (DKIM d= alanı).
#   DKIM_SELECTOR  DNS seçicisi (varsayılan hr360 -> hr360._domainkey.ALAN).
set -eu

MAIL_HOSTNAME="${MAIL_HOSTNAME:-}"
MAIL_DOMAIN="${MAIL_DOMAIN:-}"
DKIM_SELECTOR="${DKIM_SELECTOR:-hr360}"
DKIM_DIR=/var/lib/dkim

[ -n "$MAIL_HOSTNAME" ] || { echo "MAIL_HOSTNAME tanimli degil" >&2; exit 1; }
[ -n "$MAIL_DOMAIN" ] || MAIL_DOMAIN="$MAIL_HOSTNAME"
case "$DKIM_SELECTOR" in *[!A-Za-z0-9_-]*|"") echo "Gecersiz DKIM_SELECTOR" >&2; exit 1 ;; esac

# Relay izni: yalnızca konteynerin bağlı olduğu iç ağlar (ör. hr360-net 172.18.0.0/16).
networks="127.0.0.0/8"
for cidr in $(ip -o -f inet addr show | awk '$2 != "lo" {print $4}'); do
  net="$(ipcalc -n "$cidr" | cut -d= -f2)"
  networks="$networks ${net}/${cidr#*/}"
done

# DKIM anahtarı: kalıcı birimde bir kez üretilir (yeniden oluşturmada DNS kaydı değişmesin).
key="$DKIM_DIR/${DKIM_SELECTOR}.private"
if [ ! -s "$key" ]; then
  opendkim-genkey -b 2048 -h sha256 -d "$MAIL_DOMAIN" -s "$DKIM_SELECTOR" -D "$DKIM_DIR"
  echo "DKIM anahtari uretildi: ${DKIM_SELECTOR}._domainkey.${MAIL_DOMAIN}"
fi
chown -R opendkim:opendkim "$DKIM_DIR"
chmod 0700 "$DKIM_DIR"; chmod 0600 "$key"

mkdir -p /etc/opendkim
printf '%s\n' $networks > /etc/opendkim/TrustedHosts
cat > /etc/opendkim/opendkim.conf <<CONF
Syslog                  no
LogWhy                  no
UMask                   007
Mode                    s
Canonicalization        relaxed/simple
Domain                  ${MAIL_DOMAIN}
Selector                ${DKIM_SELECTOR}
KeyFile                 ${key}
InternalHosts           refile:/etc/opendkim/TrustedHosts
ExternalIgnoreList      refile:/etc/opendkim/TrustedHosts
OversignHeaders         From
Socket                  inet:8891@127.0.0.1
PidFile                 /run/opendkim/opendkim.pid
UserID                  opendkim:opendkim
CONF

postconf -e \
  "myhostname = ${MAIL_HOSTNAME}" \
  "mydomain = ${MAIL_DOMAIN}" \
  "myorigin = \$mydomain" \
  "mydestination =" \
  "local_transport = error:yerel teslim kapali" \
  "inet_interfaces = all" \
  "inet_protocols = ipv4" \
  "mynetworks = ${networks}" \
  "smtpd_relay_restrictions = permit_mynetworks, reject_unauth_destination" \
  "smtpd_recipient_restrictions = permit_mynetworks, reject" \
  "smtpd_client_restrictions = permit_mynetworks, reject" \
  "smtpd_tls_security_level = none" \
  "smtp_tls_security_level = may" \
  "smtp_tls_CAfile = /etc/ssl/certs/ca-certificates.crt" \
  "smtp_tls_loglevel = 1" \
  "message_size_limit = 26214400" \
  "maximal_queue_lifetime = 2d" \
  "bounce_queue_lifetime = 1d" \
  "smtpd_banner = \$myhostname ESMTP" \
  "disable_vrfy_command = yes" \
  "smtpd_milters = inet:127.0.0.1:8891" \
  "non_smtpd_milters = inet:127.0.0.1:8891" \
  "milter_default_action = accept" \
  "maillog_file = /dev/stdout"
# Alpine paketi submission/smtps'i kapalı getirir; yalnızca 25 dinlenir (iç ağ).
newaliases 2>/dev/null || true
postfix set-permissions >/dev/null 2>&1 || true
postfix check

opendkim -x /etc/opendkim/opendkim.conf
echo "HR360 posta servisi: ${MAIL_HOSTNAME} (DKIM ${DKIM_SELECTOR}._domainkey.${MAIL_DOMAIN}); relay agi: ${networks}"
exec postfix start-fg
