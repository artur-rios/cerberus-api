# Cerberus

## Overview

This is a system responsible to act as a vault, storing credentials, passwords and any other kind of information that needs protection.

## Platforms

The back-end is a Web API hosted on a Ubuntu VPS. The front-end is a windows desktop app, a linux desktop app, an android app and a web app that runs in the browser.

## App workflow

The user must create a account, and log-in using email and password as credentials. The app works in two modes for the desktop and android versions: default, wherr the information on the app must be available locally, so the user can access it even offline, but everything needs to be encrypted and only revealed when the user access it and encrypted again once he closes the info. And the online only mode, where the data is retrieved online only and never stored locally. The web app never stores anything on the browser. All comunication between front-end and the API must be encrypted.

## Authentication and authorization

The system uses [Heimdall API](https://github.com/artur-rios/heimdall-api) for authentication and authorization, and must handle this matter to this service. Everything else is done by Cerberus API.

## Technology

- The back-end must be built using the latest stable version of .NET and C#
- The front-end will be Flutter, shared among all suppored platforms
- All libraries and other dependencies used must be on their latest stable version
- The API must use my [Dotnet Libraries](https://artur-rios.github.io/dotnet-libraries/)

## Use Cases

- The user must be able to register, authenticate, and manage their profile through Cerberus, integrated with Heimdall
- The user must store passwords, login credentials, notes and add custom fields to each one of the records
- The custom fields can be text, numeric, boolean or hidden text fields
- The info are stored in records that are collections of key-value pairs
- The records can be organized in folders
- The records and folders can be organized in collections, and one record or folder can be in one or more collecions at the same time
- The collections can be placed in profile inside the user account. The user can choose to open only a specific profile when using the app, providing a way of showing only some information on a given device. For example he can have a profile only with his banking information, that is logged only on one specific device, then others can use a different profile with no such info
- Profiles can all be accessed using a master password or have each one their specfic password
- The user can create, read, update and delete their own accounts, profiles, folders and records
- The system must be complient with LGPD and GDPR
- The user can also be able to setup a secret manager that can be retrieved by other software services

## Inspiration

This project is inspired and aims to achieve the same functionalities of:

- Bitwarden
- AWS Secrets Manager
