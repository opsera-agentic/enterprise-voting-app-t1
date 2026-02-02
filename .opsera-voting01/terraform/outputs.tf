output "rds_address" {
  description = "RDS PostgreSQL endpoint"
  value       = aws_db_instance.postgres.address
}

output "rds_port" {
  description = "RDS PostgreSQL port"
  value       = aws_db_instance.postgres.port
}

output "rds_master_secret_arn" {
  description = "RDS master user secret ARN"
  value       = aws_db_instance.postgres.master_user_secret[0].secret_arn
}

output "elasticache_endpoint" {
  description = "ElastiCache Redis endpoint"
  value       = aws_elasticache_cluster.redis.cache_nodes[0].address
}

output "elasticache_port" {
  description = "ElastiCache Redis port"
  value       = aws_elasticache_cluster.redis.cache_nodes[0].port
}

output "irsa_vote_role_arn" {
  description = "IRSA role ARN for Vote service"
  value       = aws_iam_role.vote.arn
}

output "irsa_result_role_arn" {
  description = "IRSA role ARN for Result service"
  value       = aws_iam_role.result.arn
}

output "irsa_worker_role_arn" {
  description = "IRSA role ARN for Worker service"
  value       = aws_iam_role.worker.arn
}
