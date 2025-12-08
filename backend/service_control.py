"""
OwlSamba Service Control Utility
Provides functions to manage the OwlSamba Windows service from Python.
"""

import subprocess
import sys
import logging
from pathlib import Path

logger = logging.getLogger(__name__)


class ServiceController:
    """Control OwlSamba Windows service."""
    
    SERVICE_NAME = "OwlSamba"
    
    @staticmethod
    def _run_command(cmd: list, description: str = "") -> bool:
        """Run a command and return success status."""
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                check=False
            )
            if result.returncode == 0:
                if description:
                    logger.info(f"{description} - Success")
                return True
            else:
                error_msg = result.stderr.strip() or result.stdout.strip()
                logger.error(f"{description} - Failed: {error_msg}")
                return False
        except Exception as e:
            logger.error(f"{description} - Error: {e}")
            return False
    
    @classmethod
    def start(cls) -> bool:
        """Start the OwlSamba service."""
        return cls._run_command(
            ["net", "start", cls.SERVICE_NAME],
            "Starting service"
        )
    
    @classmethod
    def stop(cls) -> bool:
        """Stop the OwlSamba service."""
        return cls._run_command(
            ["net", "stop", cls.SERVICE_NAME],
            "Stopping service"
        )
    
    @classmethod
    def restart(cls) -> bool:
        """Restart the OwlSamba service."""
        logger.info("Restarting service...")
        if cls.stop():
            import time
            time.sleep(2)
            return cls.start()
        return False
    
    @classmethod
    def status(cls) -> str:
        """Get service status."""
        try:
            result = subprocess.run(
                ["sc", "query", cls.SERVICE_NAME],
                capture_output=True,
                text=True,
                check=False
            )
            if "RUNNING" in result.stdout:
                return "Running"
            elif "STOPPED" in result.stdout:
                return "Stopped"
            else:
                return "Unknown"
        except Exception as e:
            logger.error(f"Error checking service status: {e}")
            return "Error"
    
    @classmethod
    def is_running(cls) -> bool:
        """Check if service is running."""
        return cls.status() == "Running"


def main():
    """Command-line interface for service control."""
    import argparse
    
    parser = argparse.ArgumentParser(
        description="OwlSamba Service Control Utility",
        prog="python -m backend.service_control"
    )
    
    parser.add_argument(
        "action",
        choices=["start", "stop", "restart", "status"],
        help="Action to perform on the service"
    )
    
    args = parser.parse_args()
    controller = ServiceController()
    
    if args.action == "start":
        success = controller.start()
        sys.exit(0 if success else 1)
    
    elif args.action == "stop":
        success = controller.stop()
        sys.exit(0 if success else 1)
    
    elif args.action == "restart":
        success = controller.restart()
        sys.exit(0 if success else 1)
    
    elif args.action == "status":
        status = controller.status()
        print(f"OwlSamba Service Status: {status}")
        sys.exit(0 if status != "Error" else 1)


if __name__ == "__main__":
    main()
