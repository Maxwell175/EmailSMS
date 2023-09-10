# EmailSMS

Use an LTE modem to send/receive SMS over email.

## Why?

I needed a self-hosted solution to receiving SMS (specifically verification codes)
while traveling internationally or otherwise without home carrier connectivity.
I did not want to buy a whole smartphone and deal with the issues of having an
android OS running 24/7/365.

This is the result of an evening of tinkering with AT commands. It may not be perfect
and there is some potential code cleanup that could be done.

*The specific parts that I used were for a Raspberry Pi, but this program
should be usable with any serial modem that supports the necessary AT commands*

## Parts

- Raspberry Pi 
- Waveshare HAT SIM7600G-H (or compatible)
- Cheapo SIM card

## Setup

Setup the modem so that there is an accessible serial port to send AT commands to it.
You can test that the serial port is working by connecting using a serial console like `cu`,
and sending `AT`. You should get a response of `OK`.

Copy the `appsettings.example.json` to `appsettings.json` and fill in the `<`placeholders`>`.

Build it using `dotnet build`.

P.S. There is an included sample systemd service that can be used to set this up.
