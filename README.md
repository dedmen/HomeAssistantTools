# Project template for binary deploy
This is the project template for binary deploy. This allows you to build a binary package and deploy it to NetDaemon.

This is generated using NetDaemon runtime version 3.1 and .NET 7.


AppSettings json

{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft": "Warning"
    },
    "ConsoleThemeType": "Ansi"
  },
  "HomeAssistant": {
    "Host": "XXXX",
    "Port": 8123,
    "Ssl": false,
    "Token": "XXXX"
  },
  "NetDaemon": {
    "ApplicationConfigurationFolder": "./apps"
  },
  "Mqtt": {
    "Host": "XXXX",
    "UserName": "XXXX",
    "PassWord": "XXXX"
  },
  "CodeGeneration": {
    "Namespace": "HomeAssistantGenerated",
    "OutputFile": "HomeAssistantGenerated.cs",
    "UseAttributeBaseClasses": "false"
  },
  "Greenchoice": {
    "Username": "XXXX email here",
    "Password": "XXXX"
  }
}


