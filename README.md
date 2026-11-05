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

- `/health/live`: proceso del Gateway activo.
- `/health/ready`: conectividad con los siete servicios configurados.

## Validación

```powershell
dotnet restore SmartPalm.ApiGateway.slnx
dotnet build SmartPalm.ApiGateway.slnx --no-restore
dotnet test SmartPalm.ApiGateway.slnx --no-build
```

