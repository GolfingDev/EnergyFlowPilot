#!/usr/bin/env python3
"""Prüft nur den TCP-Zugang; sendet weder RSCP- noch Steuerbefehle."""

import argparse
import socket


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host", help="Lokale IP-Adresse oder Hostname des E3/DC")
    parser.add_argument("--port", type=int, default=5033, help="TCP-Port (Standard: 5033)")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("Der Port muss zwischen 1 und 65535 liegen.")
    try:
        with socket.create_connection((args.host, args.port), timeout=5):
            print("TCP-Verbindung möglich. Es wurden keine Anwendungsdaten gesendet.")
            print("RSCP-Anmeldung, Protokoll und Schreibrechte sind damit noch nicht bestätigt.")
        return 0
    except OSError as error:
        print(f"Keine TCP-Verbindung zum E3/DC möglich: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
