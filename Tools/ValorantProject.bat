@echo off

start "" "D:\Games\Riot Games\Riot Client\RiotClientServices.exe"

:menu

cls
color 0A
title Activate Valorant Mature Content ^(By Nguyen Tien Hung)
mode 65, 25

echo ----------- Menu -----------
echo      [1] Run the setup
echo      [2] Null
echo      [3] Oper paks folder
echo      [4] Exit
echo ----------- ==== -----------
echo: 
echo Please update Valorant to the newest version.


choice /c 1234 /n /m "And then choose an option from the menu above: "

if errorlevel 4 exit

if errorlevel 3 start "" "D:\Games\Riot Games\VALORANT\live\ShooterGame\Content\Paks" & goto :menu

if errorlevel 2 echo You chose option 2 & timeout /t 3 /nobreak >nul & goto :menu

if errorlevel 1 goto :option01

:option01
cls

if exist "D:\Games\Riot Games\VALORANT\live\ShooterGame\Content\Paks\VNGLogo-WindowsClient.*" (
    echo Mature Content is not activated, activating now...

    goto :executer
) else (
    choice /c yn /m "Mature Content file is found, do you want to reactivate it ?: "

    if errorlevel 2 (
        echo You chose No, go back to main menu after 3 seconds
        timeout /t 3 /nobreak >nul
        goto :menu
    )
    if errorlevel 1 goto :executer
)

:executer
copy /y "D:\Tool and Mod Game\Valorant\Content\MatureData-WindowsClient.*" "D:\Games\Riot Games\VALORANT\live\ShooterGame\Content\Paks"
del "D:\Games\Riot Games\VALORANT\live\ShooterGame\Content\Paks\VNGLogo-WindowsClient.*"

echo:  
echo Copy file successfully, go back to main menu after 5 seconds
timeout /t 5 /nobreak >nul
goto :menu