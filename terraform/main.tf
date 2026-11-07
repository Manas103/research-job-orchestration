terraform {
  required_providers {
    docker = {
      source  = "kreuzwerker/docker"
      version = "~> 3.0"
    }
  }
}

provider "docker" {}

resource "docker_network" "orchestration" {
  name = "research-orchestration-net"
}

resource "docker_image" "postgres" {
  name = "postgres:16-alpine"
}

resource "docker_container" "postgres" {
  name  = "research-orchestration-postgres"
  image = docker_image.postgres.image_id

  networks_advanced {
    name = docker_network.orchestration.name
  }

  env = [
    "POSTGRES_USER=orchestrator",
    "POSTGRES_PASSWORD=orchestrator",
    "POSTGRES_DB=orchestrator",
  ]

  healthcheck {
    test     = ["CMD-SHELL", "pg_isready -U orchestrator"]
    interval = "2s"
    timeout  = "2s"
    retries  = 15
  }
}

resource "docker_image" "api" {
  name = "research-orchestration-api:local"
  build {
    context  = ".."
    platform = "linux/amd64"
  }
}

resource "docker_container" "api" {
  name  = "research-orchestration-api"
  image = docker_image.api.image_id

  networks_advanced {
    name = docker_network.orchestration.name
  }

  env = [
    "DATABASE_URL=Host=research-orchestration-postgres;Port=5432;Username=orchestrator;Password=orchestrator;Database=orchestrator",
  ]

  ports {
    internal = 8080
    external = var.api_port
  }

  depends_on = [docker_container.postgres]
}

variable "api_port" {
  description = "Host port the orchestrator API is published on. Chosen explicitly rather than left to Docker's own ephemeral assignment, since the bring-up timing script needs a fixed port to poll."
  type        = number
  default     = 18080
}

output "api_url" {
  value = "http://localhost:${var.api_port}"
}
