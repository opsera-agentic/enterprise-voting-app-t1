################################################################################
# Outputs - Voting2 Infrastructure (IRSA Enabled)
################################################################################

# ECR Repositories
output "ecr_vote_repository_url" {
  description = "ECR repository URL for vote service"
  value       = aws_ecr_repository.vote.repository_url
}

output "ecr_result_repository_url" {
  description = "ECR repository URL for result service"
  value       = aws_ecr_repository.result.repository_url
}

output "ecr_worker_repository_url" {
  description = "ECR repository URL for worker service"
  value       = aws_ecr_repository.worker.repository_url
}

# RDS PostgreSQL
output "rds_endpoint" {
  description = "RDS PostgreSQL endpoint"
  value       = aws_db_instance.postgres.endpoint
}

output "rds_address" {
  description = "RDS PostgreSQL address (without port)"
  value       = aws_db_instance.postgres.address
}

output "rds_port" {
  description = "RDS PostgreSQL port"
  value       = aws_db_instance.postgres.port
}

output "rds_database_name" {
  description = "RDS database name"
  value       = aws_db_instance.postgres.db_name
}

output "rds_resource_id" {
  description = "RDS resource ID (for IAM auth)"
  value       = aws_db_instance.postgres.resource_id
}

# ElastiCache Redis
output "redis_endpoint" {
  description = "ElastiCache Redis endpoint"
  value       = aws_elasticache_cluster.redis.cache_nodes[0].address
}

output "redis_port" {
  description = "ElastiCache Redis port"
  value       = aws_elasticache_cluster.redis.cache_nodes[0].port
}

output "redis_configuration_endpoint" {
  description = "ElastiCache Redis configuration endpoint"
  value       = "${aws_elasticache_cluster.redis.cache_nodes[0].address}:${aws_elasticache_cluster.redis.cache_nodes[0].port}"
}

# IRSA Role ARNs
output "irsa_vote_role_arn" {
  description = "IAM role ARN for vote service account"
  value       = aws_iam_role.vote.arn
}

output "irsa_result_role_arn" {
  description = "IAM role ARN for result service account"
  value       = aws_iam_role.result.arn
}

output "irsa_worker_role_arn" {
  description = "IAM role ARN for worker service account"
  value       = aws_iam_role.worker.arn
}

# GitHub Actions
output "github_actions_role_arn" {
  description = "IAM role ARN for GitHub Actions OIDC"
  value       = aws_iam_role.github_actions.arn
}

# VPC Info
output "vpc_id" {
  description = "VPC ID"
  value       = data.aws_vpc.existing.id
}

output "private_subnet_ids" {
  description = "Private subnet IDs"
  value       = data.aws_subnets.private.ids
}

# EKS Info
output "eks_cluster_name" {
  description = "EKS cluster name"
  value       = data.aws_eks_cluster.existing.name
}

# Summary
output "deployment_summary" {
  description = "Summary of endpoints and roles for deployment"
  value = {
    ecr_repositories = {
      vote   = aws_ecr_repository.vote.repository_url
      result = aws_ecr_repository.result.repository_url
      worker = aws_ecr_repository.worker.repository_url
    }
    databases = {
      postgres = {
        host     = aws_db_instance.postgres.address
        port     = aws_db_instance.postgres.port
        database = aws_db_instance.postgres.db_name
        iam_auth = true
      }
      redis = {
        host = aws_elasticache_cluster.redis.cache_nodes[0].address
        port = aws_elasticache_cluster.redis.cache_nodes[0].port
      }
    }
    irsa_roles = {
      vote   = aws_iam_role.vote.arn
      result = aws_iam_role.result.arn
      worker = aws_iam_role.worker.arn
    }
    github_actions_role = aws_iam_role.github_actions.arn
  }
}
