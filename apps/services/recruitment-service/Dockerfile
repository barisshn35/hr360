FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY RecruitmentService.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
# Taban imajın düzeltmesi yayımlanmış güvenlik güncellemeleri (ör. perl-base CVE'leri) derlemede alınır.
RUN apt-get update && apt-get upgrade -y --no-install-recommends && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# .NET imajlarindaki yerlesik root olmayan kullanici (UID 1654).
USER $APP_UID
ENTRYPOINT ["dotnet", "RecruitmentService.dll"]
