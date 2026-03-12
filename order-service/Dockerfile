FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files (paths are relative to repo root build context)
COPY ["order-service/OrderService.Api/OrderService.Api.csproj", "OrderService.Api/"]
COPY ["order-service/OrderService.Service/OrderService.Service.csproj", "OrderService.Service/"]
COPY ["order-service/OrderService.Domain/OrderService.Domain.csproj", "OrderService.Domain/"]
COPY ["order-service/OrderService.Infrastructure/OrderService.Infrastructure.csproj", "OrderService.Infrastructure/"]

# Copy central package files so dotnet restore can honor Directory.Packages.props
COPY ["Directory.Packages.props", "./"]
COPY ["Directory.Build.props", "./"]

# Restore dependencies
RUN dotnet restore "OrderService.Api/OrderService.Api.csproj"

# Copy only the order-service source folders into the expected locations
COPY ["order-service/OrderService.Api/", "OrderService.Api/"]
COPY ["order-service/OrderService.Service/", "OrderService.Service/"]
COPY ["order-service/OrderService.Domain/", "OrderService.Domain/"]
COPY ["order-service/OrderService.Infrastructure/", "OrderService.Infrastructure/"]

# Build the application
WORKDIR "/src/OrderService.Api"
RUN dotnet build "OrderService.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "OrderService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create https directory for certificate mount
RUN mkdir -p /https && chmod 755 /https

ENTRYPOINT ["dotnet", "OrderService.Api.dll"]
