# POps dashboard: PHP panel on Apache, which also reverse-proxies /api, /ws, /updates and
# /download to the backend container. Built by docker-compose.yml from the repository
# root; see docs/docker.md. TLS is terminated in front of this container.
FROM php:8.3-apache

# The panel only needs curl, json and session, all built into the official image.
RUN a2enmod proxy proxy_http proxy_wstunnel headers remoteip rewrite \
    && mv "$PHP_INI_DIR/php.ini-production" "$PHP_INI_DIR/php.ini" \
    && printf 'expose_php = Off\n' > "$PHP_INI_DIR/conf.d/zz-pops.ini"

COPY docker/apache-pops.conf /etc/apache2/sites-available/000-default.conf
COPY docker/dashboard-entrypoint.sh /usr/local/bin/pops-dashboard-entrypoint
COPY Dashboard/ /var/www/html/

# includes/config.php is generated per install and not tracked in git. The template reads
# POPS_API_INTERNAL_URL / POPS_API_URL from the environment, so it is used as is.
# The panel never writes to its web root: make it root-owned and read-only for Apache
# (the base image ships /var/www/html as world-writable).
RUN chmod 0755 /usr/local/bin/pops-dashboard-entrypoint \
    && cp /var/www/html/includes/config.example.php /var/www/html/includes/config.php \
    && chown -R root:root /var/www/html \
    && chmod -R u=rwX,go=rX /var/www/html

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["curl", "-fsS", "-o", "/dev/null", "http://127.0.0.1/assets/pops_theme.css"]

ENTRYPOINT ["pops-dashboard-entrypoint"]
CMD ["apache2-foreground"]
