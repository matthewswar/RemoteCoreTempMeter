# RemoteCoreTempMeter
A basic Rainmeter CoreTemp plugin for monitoring remote server temperatures.

## Purpose
I use Rainmeter to track my CPU temperature. While their CoreTemp plugin is pretty extensive, it didn't seem to have the option to specify a remote machine. This plugin is originally meant to be a drop in replacement for the default CoreTemp plugin, but not fully featured and will only return the temperature of the desired core and speed of the CPU itself.

## Building and Running
- [Download the latest Dotnet Core](https://dotnet.microsoft.com/download)
- Run build.bat
- Take the built class file found in `RemoteCoreTempMeter\bin\Release\netstandard2.0\RemoteCoreTempMeter.dll` and paste it to `%appdata%\Rainmeter\Plugins`