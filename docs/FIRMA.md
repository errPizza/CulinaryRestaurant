# Firma de las aplicaciones

Empresa: **Culinary**. Desarrollador: **PizzaDev**. Las llaves viven en `%USERPROFILE%\.culinary-firma`, fuera de los repositorios, y las crea `tools/New-SigningKeys.ps1`, que nunca sobrescribe una existente. Esta firma es independiente de la llave raíz de licencias y de las ofertas de actualización de CulinaryServer.

| Plataforma | Firma | Identidad | Estado |
|---|---|---|---|
| Windows | Authenticode SHA-256 con sello de tiempo | `CN=Culinary, OU=PizzaDev, O=Culinary, C=MX` | Autofirmada: Windows la muestra como editor desconocido |
| Android | APK Signature Scheme v3 | `CN=PizzaDev, O=Culinary, C=MX`, vigente 30 años | Definitiva: Android no exige una CA |
| Linux | Firma GPG separada (`.deb.asc`) | `Culinary (PizzaDev)` | Definitiva; `dpkg` no la comprueba, la aplicación sigue exigiendo la oferta firmada |
| iOS | Sin firmar | Identificador `mx.culinaryrestaurant.mobile` listo | Requiere Mac, Xcode y cuenta Apple Developer |

Respalda la carpeta completa en dos medios cifrados. Si se pierde `culinary-android.jks`, ninguna instalación existente podrá actualizarse: habría que publicar otra aplicación. Android no admite cambiar de llave sin haber rotado antes con la anterior.

## Generar instaladores

```powershell
powershell -ExecutionPolicy Bypass -File tools/New-SigningKeys.ps1
powershell -ExecutionPolicy Bypass -File tools/Build-Installers.ps1 -RootPublicKey ..\CulinaryServer\secrets\root-public.pem -Windows -Android
```

Salen en `artifacts/entrega/<versión>`. `tools/Sign-Package.ps1 <archivo.deb>` firma un paquete Linux, y `build-windows.ps1` y `build-android.ps1 -Release` de CulinaryAdmin usan las mismas llaves.

El APK se compila sin recorte ni AOT para conservar los ensamblados de la compilación Debug, porque aún no se ha ejecutado en un dispositivo. Una vez probado, activar el recorte reduce su tamaño.

Tauri restaura `target/release/culinary-restaurant.exe` al terminar, sin firma: esa copia no prueba nada. Hay que comprobar los ejecutables instalados:

```powershell
Start-Process setup.exe -ArgumentList '/S','/D=C:\ruta\de\prueba' -Wait
Get-AuthenticodeSignature C:\ruta\de\prueba\*.exe
```

Con el certificado actual el estado es `UnknownError` (raíz no reconocida), que no significa alteración: esa sería `HashMismatch`.

## Certificado público para Windows

Para que Windows muestre «Culinary» como editor verificado hace falta un certificado de una CA, a nombre de una persona o empresa existente:

- Necesita acreditar una empresa registrada con ese nombre. Si el titular es una persona física, el editor mostrado será su nombre legal.
- La llave debe vivir en un token o en la nube de la CA, por lo que ya no habrá `.pfx`. Basta con instalarlo y definir `CULINARY_SIGN_THUMBPRINT`; `Sign-Package.ps1` lo usa en lugar del archivo.
- La validez máxima es de 460 días desde marzo de 2026.
- SmartScreen puede seguir avisando al principio: la reputación se acumula con las descargas.
- Azure Artifact Signing no incluye a México entre los países admitidos para organizaciones.

Hasta entonces, un equipo administrado puede confiar en el editor importando `culinary-windows.cer` en *Trusted Root* y *Trusted Publishers*. Es una decisión de cada cliente y no debe hacerla el instalador.
