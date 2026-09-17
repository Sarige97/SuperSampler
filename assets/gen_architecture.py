#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 SuperSampler 架构分层图 assets/architecture.png（PIL 绘制，中文用微软雅黑）。v2：修复视觉审查问题。"""
from PIL import Image, ImageDraw, ImageFont
import math

W, H = 1440, 980
IMG = Image.new("RGB", (W, H), "#ffffff")
D = ImageDraw.Draw(IMG)

FONT = r"C:\Windows\Fonts\msyh.ttc"
def F(size): return ImageFont.truetype(FONT, size)
f_title = F(40); f_layer = F(25); f_body = F(19); f_small = F(16); f_note = F(18)

C_HOST_BG, C_HOST_BD = "#f6f8fa", "#57606a"
C_FW_BG, C_FW_BD = "#eef4ff", "#2f6fed"
C_ABS_BG, C_ABS_BD = "#e6f4ea", "#188038"
C_CORE_BG, C_CORE_BD = "#e8f0fe", "#1a73e8"
C_DRV_BG, C_DRV_BD = "#fef3e0", "#e37400"
C_DEV_BG, C_DEV_BD = "#f3f4f6", "#6e7781"
C_CFG_BG, C_CFG_BD = "#fdf2d8", "#b08a00"
C_EVT_BG, C_EVT_BD = "#f3e8f7", "#9c27b0"
C_TXT = "#24292f"; C_GRAY = "#57606a"
ARROW = "#57606a"

def rbox(x0, y0, x1, y1, fill, outline, width=2, radius=16):
    D.rounded_rectangle([x0, y0, x1, y1], radius=radius, fill=fill, outline=outline, width=width)

def arrow(x0, y0, x1, y1, color=ARROW, width=3, both=False):
    D.line([x0, y0, x1, y1], fill=color, width=width)
    ang = math.atan2(y1 - y0, x1 - x0); L = 13
    for da in (0.42, -0.42):
        D.line([x1, y1, x1 - L * math.cos(ang - da), y1 - L * math.sin(ang - da)], fill=color, width=width)
    if both:
        for da in (0.42, -0.42):
            D.line([x0, y0, x0 + L * math.cos(ang + math.pi - da), y0 + L * math.sin(ang + math.pi - da)], fill=color, width=width)

def ctext(cx, cy, s, f, fill=C_TXT):
    b = D.textbbox((0, 0), s, font=f)
    D.text((cx - (b[2] - b[0]) / 2, cy - (b[3] - b[1]) / 2), s, font=f, fill=fill)

def ltext(x, y, s, f, fill=C_TXT):
    D.text((x, y), s, font=f, fill=fill)

# ── 标题 ──
ctext(W / 2, 46, "SuperSampler 架构分层", f_title, "#1a73e8")
ctext(W / 2, 88, "C# 上位机数据采集框架 · XML 配置驱动 · 单 DLL 可嵌入", f_body, C_GRAY)

# ── 宿主层（框架外） ──
rbox(120, 120, 1320, 205, C_HOST_BG, C_HOST_BD, 2, 14)
ctext(720, 152, "上位机应用（宿主 Host）", f_layer, C_TXT)
ctext(720, 188, "界面显示 · 权限控制 · 存储 · 业务逻辑（框架不承担，只做采集）", f_small, C_GRAY)

# ── 中带（宿主 ⇄ 框架） ──
arrow(640, 205, 640, 243, "#1a73e8", 3)                                   # 门面 API 宿主→框架
arrow(950, 245, 950, 207, "#9c27b0", 3)                                   # 事件 框架→宿主
ltext(660, 208, "门面 API：读 / 写 / 确认 / 手动重试", f_small, "#1a73e8")
ltext(975, 208, "事件订阅：值 / 报警 / 写审计", f_small, "#9c27b0")

# ── 框架容器 ──
rbox(120, 245, 1320, 790, C_FW_BG, C_FW_BD, 3, 18)
ctext(720, 276, "SuperSampler 框架（单个 DLL：Core + Abstractions + Drivers + Jint 四合一）", f_layer, "#1a73e8")

# Abstractions 契约层
rbox(150, 312, 460, 762, C_ABS_BG, C_ABS_BD, 2, 14)
ctext(305, 340, "Abstractions 契约层", f_layer, "#188038")
for i, s in enumerate(["PointValue（值/质量/时间戳）", "错误模型（8 族）", "事件契约", "IDeviceManager", "IModbusDebugTool"]):
    ctext(305, 392 + i * 68, s, f_body, "#137333")
ctext(305, 736, "契约 · 宿主可见 API", f_small, "#188038")

# Core 引擎层
rbox(490, 312, 900, 762, C_CORE_BG, C_CORE_BD, 2, 14)
ctext(695, 340, "Core 引擎层", f_layer, "#1a73e8")
for i, s in enumerate(["配置加载 · 两阶段全量校验", "调度 · 毫秒间隔 · 自动分组", "编解码 · 缩放 · 位域 · 脚本", "报警引擎（5 类 · latch/ack）", "事件总线 · 两级退避"]):
    ctext(695, 392 + i * 68, s, f_body, "#174ea6")
ctext(695, 736, "核心逻辑 · 状态与质量", f_small, "#1a73e8")

# Drivers.Modbus 驱动层
rbox(930, 312, 1290, 762, C_DRV_BG, C_DRV_BD, 2, 14)
ctext(1110, 340, "Drivers.Modbus 驱动层", f_layer, "#e37400")
for i, s in enumerate(["Modbus TCP", "RTU-over-TCP", "RTU（串口 RS485）", "帧层 · 超时 · 重试分类", "TCP keepalive"]):
    ctext(1110, 392 + i * 68, s, f_body, "#a05a00")
ctext(1110, 736, "协议实现 · 报文收发", f_small, "#e37400")

# ── 现场设备层 ──
rbox(120, 820, 1320, 915, C_DEV_BG, C_DEV_BD, 2, 14)
ctext(720, 846, "现场 Modbus 设备（PLC / 仪表 / 控制器 …）", f_layer, C_TXT)
ctext(720, 886, "TCP 网关 · RTU 串口 · 多从站（unitId）", f_small, C_GRAY)

# ── 左侧：配置输入（加宽） ──
rbox(16, 430, 116, 600, C_CFG_BG, C_CFG_BD, 2, 12)
ctext(66, 462, "HostConfig", f_small, "#7a5c00")
ctext(66, 494, "XML", f_body, "#7a5c00")
ctext(66, 530, "链路/设备", f_small, "#7a5c00")
ctext(66, 556, "点位/报警", f_small, "#7a5c00")
ctext(66, 582, "/写策略", f_small, "#7a5c00")
arrow(120, 515, 148, 515, "#b08a00", 3)

# ── 右侧：事件流出（加宽 + 短行） ──
rbox(1324, 430, 1424, 600, C_EVT_BG, C_EVT_BD, 2, 12)
ctext(1374, 462, "事件", f_body, "#7b1fa2")
ctext(1374, 496, "值变化 · 报警", f_small, "#7b1fa2")
ctext(1374, 526, "写审计 · 错误", f_small, "#7b1fa2")
ctext(1374, 556, "退避", f_small, "#7b1fa2")
arrow(1290, 515, 1322, 515, "#9c27b0", 3)

# ── 连接箭头（框架→设备） ──
arrow(695, 762, 695, 818, "#1a73e8", 3)
arrow(1110, 762, 1110, 818, "#e37400", 3)
ctext(795, 803, "Modbus TCP / RTU", f_small, C_GRAY)

# ── 底部：单 DLL 说明 ──
rbox(150, 936, 1290, 964, "#ffffff", "#d0d7de", 1, 10)
ctext(720, 950, "单 DLL 打包：dotnet build -c Release -p:EnableILRepack=true → merged/SuperSampler.Core.dll（宿主只引用这一个文件）", f_note, C_GRAY)

IMG.save(r"D:\IT\SuperModbus\SuperSampler\assets\architecture.png")
print("saved", IMG.size)
