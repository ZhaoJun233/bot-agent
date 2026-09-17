@echo off
rem ══════════════════════════════════════════════════════════════
rem  本机 pi 桥（QQ 机器人的 // 命令靠它落到你这台电脑上）
rem
rem  两种用法：
rem    ① 面板生成的一键脚本（connect-pi-bridge.cmd）—— 里面已经带好地址和令牌，直接双击那个
rem    ② 手动改这里：填机器人面板地址与令牌，然后双击本文件
rem
rem  开机自启：把本文件的快捷方式丢进
rem      %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
rem ══════════════════════════════════════════════════════════════
set PI_BRIDGE_URL=ws://bot.example.com/agent-bridge
set PI_BRIDGE_TOKEN=CHANGE_ME_与机器人_AGENT_TOKEN_一致
set PI_BRIDGE_WORKDIR=%USERPROFILE%
set PI_BRIDGE_PI=pi

title pi-bridge (QQ agent)
cd /d %~dp0
python pi-bridge.py
pause
