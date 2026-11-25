# Gateway API de SmartPalm

Punto de entrada HTTP único para los siete microservicios de SmartPalm. Conserva las rutas del monolito, valida localmente los JWT emitidos por Identity Service, aplica autorización administrativa, limita tráfico y propaga `X-Correlation-Id`.

## Rol en la plataforma

El Gateway es el único punto de entrada HTTP utilizado por la aplicación web, la aplicación móvil y las herramientas operativas. No es dueño de datos de negocio: autentica usuarios humanos con JWT, aplica políticas de rutas, reenvía las solicitudes al servicio dueño y conserva un contrato público estable mientras los servicios evolucionan de forma independiente.

El flujo físico de datos está dividido intencionalmente: Edge API envía las lecturas de sensores a la ruta de Ingestion a través del Gateway, mientras que el registro y la provisión de dispositivos utilizan rutas administrativas de Device. El Gateway nunca recibe ni almacena la clave operativa del sensor como dato de aplicación.

| Cluster | URL local predeterminada | Contratos principales |
|---|---:|---|
| Identity | `http://localhost:5101` | Autenticación, usuarios y suscripciones |
| Crop | `http://localhost:5102` | Plantaciones, sectores y afiliaciones |
| Device | `http://localhost:5103` | Gateways Edge y dispositivos IoT |
| Ingestion | `http://localhost:5104` | Lecturas de sensores, salud y umbrales |
| Alert | `http://localhost:5105` | Alertas y configuración de notificaciones |
| Agronomy | `http://localhost:5106` | Recomendaciones e informes |
| Field | `http://localhost:5107` | Visitas, inspecciones e intervenciones |

Las rutas de ingestión del Edge API y la consulta de conectividad permanecen anónimas para preservar el flujo físico existente. El resto utiliza la política JWT predeterminada, salvo los endpoints administrativos, que requieren el rol `Administrator`.

## Relaciones entre clientes y servicios

- **Identity:** inicio de sesión, usuarios, planes, suscripciones y pagos.
- **Crop:** plantaciones, sectores y afiliaciones de agrónomos.
- **Device:** gateways, registro IoT, sincronización del registry y provisión/revocación.
- **Ingestion:** lecturas, salud de sectores y umbrales de sensores.
- **Alert:** alertas y configuración de notificaciones.
- **Agronomy/Field:** recomendaciones, informes, visitas, inspecciones e intervenciones.

Consulta el README del servicio dueño para conocer la propiedad de datos y la ejecución local. Ningún cliente debe llamar directamente a la base de datos de un microservicio ni omitir la autorización del Gateway.

## Ejecución local

```powershell
Copy-Item .env.example .env
dotnet run --project src/SmartPalm.ApiGateway
```

El Gateway escucha en `http://localhost:5100`. Como contenedor: `docker compose -f compose.local.yaml up --build`.

## Despliegue en Coolify/Vultr

Usar el Dockerfile del repositorio y configurar el puerto interno `8080`. Exponer solamente el Gateway mediante el proxy de Coolify; no publicar puertos individuales de los microservicios.

Variables obligatorias de ejecución en Coolify:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
JWT_ISSUER=smartpalm.identity
JWT_AUDIENCE=smartpalm.clients
JWT_KEY=<clave compartida de al menos 32 caracteres>
IDENTITY_SERVICE_URL=http://<nombre-interno-o-ip-privada>:8080
CROP_SERVICE_URL=http://<nombre-interno-o-ip-privada>:8080
DEVICE_SERVICE_URL=http://<nombre-interno-o-ip-privada>:8080
INGESTION_SERVICE_URL=http://<nombre-interno-o-ip-privada>:8080
ALERT_SERVICE_URL=http://<nombre-interno-o-ip-privada>:8080
AGRONOMY_SERVICE_URL=http://<nombre-interno-o-ip-privada>:8080
FIELD_SERVICE_URL=http://<nombre-interno-o-ip-privada>:8080
```

El secreto `JWT_KEY` debe coincidir exactamente con Identity Service. Configurar las URLs mediante nombres internos resueltos por Coolify o direcciones privadas de Vultr; no usar `localhost`, `host.docker.internal` ni puertos públicos.

En Docker Compose de Coolify se puede usar `compose.production.yaml`. Coolify debe declarar las variables anteriores como secretos de ejecución. El health check puede apuntar a `/health/live` y el check con dependencias a `/health/ready`.

- `/health/live`: proceso del Gateway activo.
- `/health/ready`: conectividad con los siete servicios configurados.

## Validación

```powershell
dotnet restore SmartPalm.ApiGateway.slnx
dotnet build SmartPalm.ApiGateway.slnx --no-restore
dotnet test SmartPalm.ApiGateway.slnx --no-build
```

