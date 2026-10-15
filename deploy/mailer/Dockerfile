# Isteğe bağlı "sunucu kendisi göndersin" posta servisi (install.sh --mail-mode=self).
# Yalnızca giden posta: HR360 servisleri iç ağdan (hr360-net) kimlik doğrulamasız
# bırakır, Postfix alıcıların MX sunucularına doğrudan teslim eder; OpenDKIM imzalar.
# Dışarıya port açılmaz; relay yalnızca konteynerin bağlı olduğu iç ağlara izinlidir.
# Postfix master süreci root olarak başlar ve smtpd/qmgr/smtp süreçlerini kendisi
# "postfix" kullanıcısına düşürür; OpenDKIM de "opendkim" kullanıcısıyla çalışır.
FROM alpine:3.24

RUN apk add --no-cache postfix opendkim opendkim-utils ca-certificates tzdata \
 && mkdir -p /var/lib/dkim /run/opendkim \
 && chown opendkim:opendkim /var/lib/dkim /run/opendkim

COPY entrypoint.sh /usr/local/bin/hr360-mailer
RUN chmod 0755 /usr/local/bin/hr360-mailer

EXPOSE 25
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD postfix status >/dev/null 2>&1 || exit 1

ENTRYPOINT ["/usr/local/bin/hr360-mailer"]
