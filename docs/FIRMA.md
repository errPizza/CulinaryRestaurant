# Firma de las aplicaciones

Empresa: **Culinary Services**. Desarrollador: **PizzaDev**. Las llaves viven en `%USERPROFILE%\Desktop\.culinary-firma` (los scripts también buscan en `%USERPROFILE%\.culinary-firma`), fuera de los repositorios, y las crea `tools/New-SigningKeys.ps1`, que nunca sobrescribe una existente. Esta firma es independiente de la llave raíz de licencias y de las ofertas de actualización de CulinaryServer.

| Plataforma | Firma | Identidad | Estado |
|---|---|---|---|
| Windows | Authenticode SHA-256 con sello de tiempo | `CN=Culinary Services, OU=PizzaDev, O=Culinary Services, C=MX` | Autofirmada: Windows la muestra como editor desconocido |
| Android | APK Signature Scheme v3 con rotación | `CN=PizzaDev, O=Culinary Services, C=MX`, encadenada a `CN=PizzaDev, O=Culinary, C=MX` | Definitiva: Android no exige una CA |
| Linux | Firma GPG separada (`.deb.asc`) | `Culinary Services (PizzaDev)`, misma llave que `Culinary (PizzaDev)` | Definitiva; `dpkg` no la comprueba, la aplicación sigue exigiendo la oferta firmada |
| iOS | Sin firmar | Identificador `mx.culinaryrestaurant.mobile` listo | Requiere Mac, Xcode y cuenta Apple Developer |

Respalda la carpeta completa en dos medios cifrados. Si se pierde `culinary-android.jks`, `culinary-services-android.jks` o `culinary-android.lineage`, ninguna instalación existente podrá actualizarse: habría que publicar otra aplicación.

## Cambio de nombre a Culinary Services (2026-11-06)

- **Windows:** certificado nuevo; el anterior está en `anterior-culinary-2026-11-02\`. Quien haya confiado en el viejo debe importar el nuevo `culinary-windows.cer`. El instalador encuentra la versión anterior por el nombre del producto, así que se actualiza encima.
- **Android:** `apksigner rotate` creó `culinary-android.lineage`, de la llave original a `culinary-services-android.jks`. `tools/Sign-Apk.ps1` firma v1/v2 con la original y v3 con la nueva desde Android 9 (`--rotation-min-sdk-version 28`): lo instalado se actualiza sin desinstalar. Nunca firmar sólo con la llave nueva.
- **Linux:** a la misma llave GPG se le agregó la identidad `Culinary Services (PizzaDev)` como principal; las firmas anteriores siguen verificando.

## Generar instaladores

```powershell
powershell -ExecutionPolicy Bypass -File tools/New-SigningKeys.ps1
powershell -ExecutionPolicy Bypass -File tools/Build-Installers.ps1 -RootPublicKey ..\CulinaryServer\secrets\root-public.pem -Windows -Android
```

Salen en `artifacts/entrega/<versión>`. `tools/Sign-Package.ps1 <archivo.deb>` firma un paquete Linux, y `build-windows.ps1` y `build-android.ps1 -Release` de CulinaryAdmin y CulinaryDueño usan las mismas llaves (los APK, con `tools/Sign-Apk.ps1`).

El APK se compila sin recorte ni AOT para conservar los ensamblados de la compilación Debug, porque aún no se ha ejecutado en un dispositivo. Una vez probado, activar el recorte reduce su tamaño.

Tauri restaura `target/release/culinary-restaurant.exe` al terminar, sin firma: esa copia no prueba nada. Hay que comprobar los ejecutables instalados:

```powershell
Start-Process setup.exe -ArgumentList '/S','/D=C:\ruta\de\prueba' -Wait
Get-AuthenticodeSignature C:\ruta\de\prueba\*.exe
```

Con el certificado actual el estado es `UnknownError` (raíz no reconocida), que no significa alteración: esa sería `HashMismatch`.

## Certificado público para Windows

Para que Windows muestre «Culinary Services» como editor verificado hace falta un certificado de una CA, a nombre de una persona o empresa existente:

- Necesita acreditar una empresa registrada con ese nombre. Si el titular es una persona física, el editor mostrado será su nombre legal.
- La llave debe vivir en un token o en la nube de la CA, por lo que ya no habrá `.pfx`. Basta con instalarlo y definir `CULINARY_SIGN_THUMBPRINT`; `Sign-Package.ps1` lo usa en lugar del archivo.
- La validez máxima es de 460 días desde marzo de 2026.
- SmartScreen puede seguir avisando al principio: la reputación se acumula con las descargas.
- Azure Artifact Signing no incluye a México entre los países admitidos para organizaciones.

Hasta entonces, un equipo administrado puede confiar en el editor importando `culinary-windows.cer` en *Trusted Root* y *Trusted Publishers*. Es una decisión de cada cliente y no debe hacerla el instalador.
