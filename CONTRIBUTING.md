# Contributing to POps

First off, thank you for considering contributing to POps! We believe that open source and community collaboration are what make software truly great. 

This document provides guidelines for contributing to the Pasha Operations Platform.

## Code of Conduct

By participating in this project, you are expected to uphold our [Code of Conduct](CODE_OF_CONDUCT.md). Please report any unacceptable behavior to `opensource@pashacore.com.tr`.

## How Can I Contribute?

### 1. Reporting Bugs
- Check the [Issues](https://github.com/PashaCore/POps/issues) to ensure the bug hasn't already been reported.
- If it hasn't, open a new issue. Include your OS, Agent version, Backend version, and clear steps to reproduce the issue.
- If the bug is a security vulnerability, please refer to our [Security Policy](SECURITY.md).

### 2. Suggesting Enhancements
- Enhancement suggestions are tracked as GitHub issues. 
- Please provide a clear title and description, and explain how the enhancement would improve the workflow for lab administrators or enterprise IT.

### 3. Submitting Pull Requests
- Fork the repo and create your branch from `main`.
- If you've added code that should be tested, add tests.
- Ensure the code follows the existing style:
  - C# (Agent): Follow standard Microsoft C# naming conventions.
  - Python (Backend): PEP 8 compliant.
  - PHP (Dashboard): PSR-12 compliant.
- Issue that pull request!

## Local Development Setup

To set up a local development environment, please refer to our [Installation Guide](docs/installation.md) in the `docs/` directory.

You will need:
- `.NET 8 SDK` (for the Agent)
- `Python 3.12+` and `PostgreSQL` (for the Backend)
- `PHP 8.2+` and a Web Server like Apache/Nginx (for the Dashboard)

Thank you for helping us make POps better!
