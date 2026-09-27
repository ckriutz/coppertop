# The SDK runs on the build machine's native arch and cross-compiles for TARGETARCH (no slow QEMU builds).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Coppertop.Research.csproj .
RUN dotnet restore -a $TARGETARCH
COPY . .
RUN dotnet publish -c Release -a $TARGETARCH -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
USER $APP_UID
ENV DOTNET_ENVIRONMENT=Production \
    CoppertopApi__BaseUrl=http://api:8080
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Coppertop.Research.dll"]
