# The SDK runs on the build machine's native arch and cross-compiles for TARGETARCH (no slow QEMU builds).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Coppertop.Api.csproj .
RUN dotnet restore -a $TARGETARCH
COPY . .
RUN dotnet publish -c Release -a $TARGETARCH -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Coppertop="Data Source=/data/coppertop.db"
VOLUME /data
EXPOSE 8080
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Coppertop.Api.dll"]
