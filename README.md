# Project template for binary deploy
This is the project template for binary deploy. This allows you to build a binary package and deploy it to NetDaemon.

This is generated using NetDaemon runtime version 3.1 and .NET 7.

Contains
- Greenchoice meter reading
- KPN Unlimited contract, reading how much data is used/available, and automatically "purchasing" free 2Gb extensions


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
  },
  "KPN": {
    "Username": "XXXX email here",
    "Password": "XXXX",
    "Number": "Contract phone number",
    "SubscriptionPlanId":"Subscription plan ID (get it some way, no time to explain)",
    "SomeHashThing": "Find it from mijnkpnpref cookie, the first hash value in there before mobile number"
  }
}


