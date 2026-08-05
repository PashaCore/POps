# Security Architecture

POps authenticates agents using immutable Hardware IDs (HWID). The dashboard uses JSON Web Tokens (JWT) for Role-Based Access Control (RBAC). No keyloggers are implemented, and remote sessions notify the active user.