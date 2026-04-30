#Платформа онлайн-тестирования Star-Study.
**Сайт:** [star-study.ru](https://star-study.ru)

### Запуск с сертификатом
```bash
sudo apt update && sudo apt install -y git nginx certbot python3-certbot-nginx && cd /opt && sudo git clone https://github.com/dlldotusha/star-study-repo.git star-study && sudo chown -R "$USER":"$USER" /opt/star-study && cd /opt/star-study && cp .env.example .env

# Настройте .env перед запуском

docker compose up -d --build && docker compose ps
