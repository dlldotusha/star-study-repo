# Star-Study Docker

Подготовка на Ubuntu 24:

```bash
sudo apt update
sudo apt install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
```

Настройка секретов:

```bash
cp .env.example .env
nano .env
```

Обязательно заполните `POSTGRES_PASSWORD`, `STAR_STUDY_ADMIN_LOGIN` и `STAR_STUDY_ADMIN_PASSWORD`. Без них `docker compose` не стартует. Репозиторий поставляется без учебных пользователей, предзаполненных тестов и слабого дефолтного админа.

Запуск:

```bash
docker compose up -d --build
```

Проверка:

```bash
curl http://localhost:8080/healthz
```

Адреса:

```text
http://localhost:8080/test
http://localhost:8080/admin
```

PostgreSQL поднимается сервисом `postgres`. Приложение получает строку подключения через `ConnectionStrings__Postgres`, создаёт таблицу `app_state` и хранит там тесты, учеников, группы, сессии, токены, ответы и ручные проверки.
