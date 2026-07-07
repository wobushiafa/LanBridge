# ==========================================
# 1. 编译阶段 (Build Stage)
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# 安装 Native AOT 编译所需的系统依赖
RUN apt-get update && apt-get install -y --no-install-recommends \
    clang \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# 复制整个项目（包含依赖的 LanBridge.Common 项目）
COPY . .

# 编译并发布 SignalingServer
RUN dotnet publish src/LanBridge.SignalingServer/LanBridge.SignalingServer.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -o /app

# ==========================================
# 2. 运行阶段 (Runtime Stage)
# ==========================================
# 使用微软官方专为 Native AOT/自包含应用提供的极简运行环境（不含 .NET 运行时，仅含基础依赖库如 glibc, openssl）
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app

# 从编译阶段拷贝生成的二进制文件
COPY --from=build /app/LanBridge.SignalingServer .

# 暴露信令服务相关端口
# 9000: 信令控制 TCP 端口
# 9001: 默认 STUN 主服务 UDP 端口
# 9002: 默认 Relay 中继服务 UDP 端口
# 9003: 默认 STUN 备用服务 UDP 端口
EXPOSE 9000/tcp
EXPOSE 9001/udp
EXPOSE 9002/udp
EXPOSE 9003/udp

# 默认启动命令
ENTRYPOINT ["./LanBridge.SignalingServer"]
CMD ["-c", "/app/config/server.config.json"]
