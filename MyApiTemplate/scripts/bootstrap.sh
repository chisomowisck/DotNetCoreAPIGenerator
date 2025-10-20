@echo off
REM Usage: scripts\bootstrap.cmd "<provider>" "<conn>" "<context>" "<project>"
setlocal

set PROVIDER=%~1
set CONN=%~2
set CTX=%~3
set PROJ=%~4

if "%PROJ%"=="" set PROJ=.

REM Ensure swagger + EF packages (idempotent)
dotnet add "%PROJ%" package Swashbuckle.AspNetCore
dotnet add "%PROJ%" package Microsoft.EntityFrameworkCore.Design
dotnet add "%PROJ%" package %PROVIDER%

REM Run generator
db2crud --provider "%PROVIDER%" --conn "%CONN%" --context-name "%CTX%" --project "%PROJ%"
endlocal
